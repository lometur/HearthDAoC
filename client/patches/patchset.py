"""Load, check, apply and restore a HearthDAoC client patch set (format 1).

A patch set is a JSON file. For each client file, given by its path relative to the client
folder, it holds the SHA-256 the file must have before and after patching and the operations
that turn one into the other. It carries only our patch data: the EA files stay on the
player's machine and are never distributed.

Operations, applied in order to an in-memory copy of the file:
- replace: at "offset" in the current bytes, "from" (hex) must match exactly; it becomes "to"
  (hex, the same length).
- append: "data" (hex) is added at the end.
- text-replace: on the bytes decoded as Latin-1, "find" must occur exactly once; it becomes
  "replace". Every other byte, CRLF line ends included, is kept.
- file: the whole file becomes <bundle folder>/<source>. "after": "source" means "equals that
  bundled file".

Rules (client/windows/patch-client.ps1 follows the same ones):
- A file whose SHA-256 is its "after" hash is already patched and is skipped.
- A file that is missing, or whose hash is neither "before" nor "after", is refused. Then
  nothing at all is changed: every file is checked, and every new content built and verified,
  before anything is written.
- The original is backed up once as <file>.hearthdaoc-orig. A backup that isn't the original
  is replaced by the verified original.
- A restore puts a backup back only over the patched file. A file that has changed since it was
  patched (for example a newer client) or is missing is refused, and then nothing is restored.
- Every write is atomic: a temporary file in the same folder, then os.replace.
- Only the patch set's own relative paths inside the client folder are touched.
"""
import hashlib
import json
import os
import re
import stat
import tempfile

FORMAT = 1
BACKUP_SUFFIX = ".hearthdaoc-orig"
OPS = {
    "replace": ("offset", "from", "to"),
    "append": ("data",),
    "text-replace": ("find", "replace"),
    "file": ("source",),
}
_SHA256 = re.compile(r"[0-9a-f]{64}")
_HEX = re.compile(r"(?:[0-9a-f]{2})+")


class PatchError(Exception):
    """An invalid patch set, a refused client file or a failed check; the message says which."""


def _check_path(rel):
    """Accept only a relative path with forward slashes that stays inside its folder."""
    if not isinstance(rel, str) or not rel or rel.startswith("/") or "\\" in rel or ":" in rel:
        raise PatchError(f"unsafe path: {rel!r}")
    if any(part in ("", ".", "..") for part in rel.split("/")):
        raise PatchError(f"unsafe path: {rel!r}")
    return rel


def _hex(value, where):
    if not isinstance(value, str) or not _HEX.fullmatch(value):
        raise PatchError(f"{where}: not lower-case hex bytes")
    return bytes.fromhex(value)


def _is_sha256(value):
    return isinstance(value, str) and _SHA256.fullmatch(value) is not None


def _check_op(op, where):
    if not isinstance(op, dict) or op.get("op") not in OPS:
        raise PatchError(f"{where}: unknown operation")
    kind = op["op"]
    for key in OPS[kind]:
        if key not in op:
            raise PatchError(f"{where}: {kind} needs {key!r}")
    if kind == "replace":
        if type(op["offset"]) is not int or op["offset"] < 0:
            raise PatchError(f"{where}: offset must be a whole number, 0 or more")
        if len(_hex(op["from"], where)) != len(_hex(op["to"], where)):
            raise PatchError(f"{where}: 'from' and 'to' must have the same length")
    elif kind == "append":
        _hex(op["data"], where)
    elif kind == "text-replace":
        for key in ("find", "replace"):
            if not isinstance(op[key], str):
                raise PatchError(f"{where}: {key!r} must be text")
            try:
                op[key].encode("latin-1")
            except UnicodeEncodeError:
                raise PatchError(f"{where}: {key!r} has characters outside Latin-1") from None
        if not op["find"]:
            raise PatchError(f"{where}: 'find' is empty")
    else:
        _check_path(op["source"])


def load(path):
    """Read and validate a patch set; raise PatchError if it can't be used."""
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, ValueError) as e:
        raise PatchError(f"cannot read patch set {path}: {e}") from e
    if not isinstance(data, dict) or type(data.get("format")) is not int or data["format"] != FORMAT:
        raise PatchError(f"{path}: not a format {FORMAT} patch set")
    files = data.get("files")
    if not isinstance(files, list) or not files:
        raise PatchError(f"{path}: the patch set lists no files")
    seen = set()
    for n, entry in enumerate(files, 1):
        if not isinstance(entry, dict):
            raise PatchError(f"file {n}: not an object")
        rel = _check_path(entry.get("path"))
        if rel.lower() in seen:
            raise PatchError(f"{rel}: listed twice")
        seen.add(rel.lower())
        before, after, ops = entry.get("before"), entry.get("after"), entry.get("ops")
        if not _is_sha256(before):
            raise PatchError(f"{rel}: 'before' must be a lower-case SHA-256")
        if not (_is_sha256(after) or after == "source"):
            raise PatchError(f"{rel}: 'after' must be a lower-case SHA-256 or \"source\"")
        if before == after:
            raise PatchError(f"{rel}: 'before' and 'after' are the same")
        if not isinstance(ops, list) or not ops:
            raise PatchError(f"{rel}: no operations")
        for i, op in enumerate(ops, 1):
            _check_op(op, f"{rel} op {i}")
        if after == "source" and (len(ops) != 1 or ops[0]["op"] != "file"):
            raise PatchError(f"{rel}: \"after\": \"source\" needs exactly one file operation")
    return data


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 22), b""):
            h.update(block)
    return h.hexdigest()


def _target(client_dir, rel):
    return os.path.join(client_dir, *rel.split("/"))


def _bundled(bundle_dir, rel):
    path = os.path.join(bundle_dir or "", *rel.split("/"))
    if bundle_dir is None or not os.path.isfile(path):
        raise PatchError(f"bundled file missing: {path}")
    return path


def _after_hash(entry, bundle_dir):
    if entry["after"] == "source":
        return sha256_file(_bundled(bundle_dir, entry["ops"][0]["source"]))
    return entry["after"]


def transform(data, ops, bundle_dir):
    """Apply ops in order to a copy of data and return the new bytes."""
    out = bytearray(data)
    for i, op in enumerate(ops, 1):
        kind = op["op"]
        if kind == "replace":
            start, old, new = op["offset"], bytes.fromhex(op["from"]), bytes.fromhex(op["to"])
            if out[start:start + len(old)] != old:
                raise PatchError(f"op {i} (replace at {start:#x}): the bytes there are not the expected ones")
            out[start:start + len(old)] = new
        elif kind == "append":
            out += bytes.fromhex(op["data"])
        elif kind == "text-replace":
            text = out.decode("latin-1")
            count = text.count(op["find"])
            if count != 1:
                raise PatchError(f"op {i} (text-replace): found the text {count} times, not exactly once")
            out = bytearray(text.replace(op["find"], op["replace"]).encode("latin-1"))
        elif kind == "file":
            with open(_bundled(bundle_dir, op["source"]), "rb") as f:
                out = bytearray(f.read())
        else:
            raise PatchError(f"op {i}: unknown operation {kind!r}")
    return bytes(out)


def status(client_dir, patchset, bundle_dir):
    """[(path, state)] per file, state "unpatched", "patched", "unknown" or "missing"."""
    out = []
    for entry in patchset["files"]:
        target = _target(client_dir, entry["path"])
        if not os.path.isfile(target):
            out.append((entry["path"], "missing"))
            continue
        digest = sha256_file(target)
        if digest == _after_hash(entry, bundle_dir):
            out.append((entry["path"], "patched"))
        elif digest == entry["before"]:
            out.append((entry["path"], "unpatched"))
        else:
            out.append((entry["path"], "unknown"))
    return out


def _write_atomic(path, data, mode):
    """Write data to path through a temporary file in the same folder, then os.replace."""
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(path) or ".", prefix=".hearthdaoc-", suffix=".tmp")
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        os.chmod(tmp, mode)
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except FileNotFoundError:
            pass
        raise


def apply(client_dir, patchset, bundle_dir):
    """Patch every file that isn't patched yet; [(path, "patched" | "already")] per file.

    An unknown or missing file raises PatchError, listing every such file, and nothing is
    changed. Every new content is built and checked against its "after" hash before the first
    write.
    """
    states = status(client_dir, patchset, bundle_dir)
    refused = [f"{path} ({state})" for path, state in states if state in ("unknown", "missing")]
    if refused:
        raise PatchError("refused, nothing changed: " + ", ".join(refused))
    work = []
    for entry, (path, state) in zip(patchset["files"], states):
        if state == "patched":
            continue
        target = _target(client_dir, path)
        with open(target, "rb") as f:
            original = f.read()
        if hashlib.sha256(original).hexdigest() != entry["before"]:
            raise PatchError(f"{path} changed while it was being checked; nothing changed")
        new = transform(original, entry["ops"], bundle_dir)
        if hashlib.sha256(new).hexdigest() != _after_hash(entry, bundle_dir):
            raise PatchError(f"{path}: the patched file doesn't have the expected SHA-256; nothing changed")
        work.append((target, original, new, entry["before"]))
    for target, original, new, before in work:
        mode = stat.S_IMODE(os.stat(target).st_mode)
        backup = target + BACKUP_SUFFIX
        if not (os.path.isfile(backup) and sha256_file(backup) == before):
            _write_atomic(backup, original, mode)
        _write_atomic(target, new, mode)
    return [(path, "already" if state == "patched" else "patched") for path, state in states]


def restore_status(client_dir, patchset, bundle_dir):
    """[(path, state)] per file, in patch-set order, for a restore. The states:

    "patched" (the backup is the original and the file is the patched one: it can be restored),
    "no-backup", "original" (the file already is the original; its backup is kept),
    "wrong-backup" (the backup's hash isn't "before"), "missing" (the backup is fine but the file
    is gone) and "changed" (the backup is fine but the file is neither the original nor the
    patched one, for example a newer client installed after patching).
    """
    out = []
    for entry in patchset["files"]:
        target = _target(client_dir, entry["path"])
        backup = target + BACKUP_SUFFIX
        if not os.path.isfile(backup):
            state = "no-backup"
        elif sha256_file(backup) != entry["before"]:
            state = "wrong-backup"
        elif not os.path.isfile(target):
            state = "missing"
        else:
            digest = sha256_file(target)
            if digest == entry["before"]:
                state = "original"
            elif digest == _after_hash(entry, bundle_dir):
                state = "patched"
            else:
                state = "changed"
        out.append((entry["path"], state))
    return out


def restore(client_dir, patchset, bundle_dir):
    """Move each backup back over its patched file; [(path, "restored" | "not-patched")] per file.

    A backup goes back only over the patched file. A file that already is the original keeps its
    backup and counts as "not-patched", like a file without a backup. A wrong backup, or a file
    that is missing or has changed since it was patched, raises PatchError, listing every such
    file, and nothing is changed.
    """
    states = restore_status(client_dir, patchset, bundle_dir)
    problems = []
    for state, label, suffix in (("wrong-backup", "not the original file", BACKUP_SUFFIX),
                                 ("changed", "changed since it was patched", ""), ("missing", "missing", "")):
        paths = [path + suffix for path, s in states if s == state]
        if paths:
            problems.append(f"{label}: " + ", ".join(paths))
    if problems:
        raise PatchError("; ".join(problems))
    out = []
    for path, state in states:
        if state == "patched":
            target = _target(client_dir, path)
            os.replace(target + BACKUP_SUFFIX, target)
            out.append((path, "restored"))
        else:
            out.append((path, "not-patched"))
    return out
