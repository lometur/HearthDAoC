; HearthDAoC classic character creation: the code cave in game.dll's .hdcc section.
;
; build.py assembles this file with nasm (-f bin -D HDCC_ORG=<VA of the .hdcc section>) next to
; baseclass_data.inc, which it generates from the server's class files and src/base_classes.py.
; Target: the OfflineDAoC 0.35 classic game.dll, whose character creation code is stock 1.127.
;
; The hook at 0x5B0051 turns "call 0x5B438C" in the creation-registry builder (0x5B0031) into
; "call hdc_post". The builder has just pushed the registry (ebx), so hdc_post is stdcall with
; that one argument. It runs every time pregame opens, after the three realms have registered
; their races and final classes, and:
;   1. makes the displaced call;
;   2. hides the final classes and the races after Shrouded Isles by zeroing their availability
;      masks. The creation screen's filters (classes 0x59F774, races 0x59F729) skip any object
;      whose mask has no bit in common with the server type (ctx+0x50), so hidden objects get
;      no button, no label and are never picked at random;
;   3. registers each base class the way a stock registration block does (Armsman's runs from
;      0x5B0D0F to 0x5B0DE3).

BITS 32
%ifndef HDCC_ORG
%error "assemble with -D HDCC_ORG=<VA of the .hdcc section>"
%endif
org HDCC_ORG

; game.dll functions
STATDESC_INIT   equ 0x5B438C  ; the displaced call: stdcall(registry)
REGISTER_CLASS  equ 0x5B01A3  ; ecx = &entry; stdcall(registry, id, realm, &stat ids, &stat amounts,
                              ;   race vector by value (16 bytes), stat count); ret 0x28
VEC_PUSH_BACK   equ 0x5B4A65  ; eax = &vector; stdcall(&value)
VEC_COPY        equ 0x520E28  ; ecx = &source vector; stdcall(&destination)
VEC_FREE        equ 0x45BDC3  ; esi = &vector

; The creation registry holds two tables of object pointers indexed by id. Each is the begin
; pointer of a std::vector, with the end pointer 4 bytes later.
REG_RACES       equ 0x28      ; race objects by race id (lookup 0x5B485A)
REG_CLASSES     equ 0x38      ; class objects by class id (lookup 0x5B4842)
RACE_MASK       equ 0x54      ; race object: availability mask (race filter 0x59F729)
CLASS_MASK      equ 0x30      ; class object: availability mask (class filter 0x59F774)

; What every base class registers with (the entry fields; REGISTER_CLASS copies them at 0x5B01E2)
EXPANSION       equ 0         ; entry +0x08: classic
TRIAL           equ 1         ; entry +0x0C: allowed on trial accounts
MASK            equ 7         ; entry +0x10: listed on every server type
GENDER          equ 0         ; entry +0x14: both genders
DESC_ID         equ 0         ; entry +0x18: no string id, so the text at +0x1C is shown as written
STAT_COUNT      equ 3
AMOUNT          equ 10        ; auto-assign recipe: +10 to each highlighted stat, 30 points. Patch P1
                              ; stops auto-assign; the stat ids only colour the stats.

; A base_table record in baseclass_data.inc, all dwords. The table ends with a 0 class id.
REC_ID          equ 0x00
REC_REALM       equ 0x04      ; 1 Albion, 2 Midgard, 3 Hibernia
REC_NAME_ID     equ 0x08      ; name string id
REC_NAME        equ 0x0C      ; name pointer (the client's own .rdata string)
REC_DESC        equ 0x10      ; description pointer
REC_STATS       equ 0x14      ; 3 stat ids (0 STR, 1 CON, 2 DEX, 3 QUI, 4 INT, 5 PIE, 6 EMP, 7 CHA)
REC_RACE_COUNT  equ 0x20
REC_RACES       equ 0x24      ; the race ids

; hdc_post's stack frame: the same layout as a stock registration block
ENTRY           equ -0x50     ; the 0x20-byte entry REGISTER_CLASS reads through ecx
RACE_VECTOR     equ -0x30     ; std::vector<int>: allocator, begin, end, end of storage
STAT_IDS        equ -0x20     ; 3 dwords
RACE_ID         equ -0x14     ; the value VEC_PUSH_BACK copies
STAT_AMOUNTS    equ -0x10     ; 3 bytes

hdc_post:
    push ebp
    mov ebp, esp
    sub esp, 0x50
    push ebx
    push esi
    push edi
    mov ebx, [ebp+8]                    ; the registry
    push ebx
    call STATDESC_INIT

    mov esi, hide_classes
    mov edi, REG_CLASSES
    mov edx, CLASS_MASK
    call hide
    mov esi, hide_races
    mov edi, REG_RACES
    mov edx, RACE_MASK
    call hide

    mov edi, base_table
.next_class:
    cmp dword [edi+REC_ID], 0
    je .done
    mov eax, [edi+REC_NAME_ID]
    mov [ebp+ENTRY+0x00], eax
    mov eax, [edi+REC_NAME]
    mov [ebp+ENTRY+0x04], eax
    mov dword [ebp+ENTRY+0x08], EXPANSION
    mov dword [ebp+ENTRY+0x0C], TRIAL
    mov dword [ebp+ENTRY+0x10], MASK
    mov dword [ebp+ENTRY+0x14], GENDER
    mov dword [ebp+ENTRY+0x18], DESC_ID
    mov eax, [edi+REC_DESC]
    mov [ebp+ENTRY+0x1C], eax
    mov eax, [edi+REC_STATS+0]
    mov [ebp+STAT_IDS+0], eax
    mov eax, [edi+REC_STATS+4]
    mov [ebp+STAT_IDS+4], eax
    mov eax, [edi+REC_STATS+8]
    mov [ebp+STAT_IDS+8], eax
    mov byte [ebp+STAT_AMOUNTS+0], AMOUNT
    mov byte [ebp+STAT_AMOUNTS+1], AMOUNT
    mov byte [ebp+STAT_AMOUNTS+2], AMOUNT
    xor eax, eax                        ; an empty race vector
    mov [ebp+RACE_VECTOR+4], eax
    mov [ebp+RACE_VECTOR+8], eax
    mov [ebp+RACE_VECTOR+12], eax
    mov ecx, [edi+REC_RACE_COUNT]
    lea esi, [edi+REC_RACES]
.next_race:
    test ecx, ecx
    jz .register
    push ecx
    mov eax, [esi]
    mov [ebp+RACE_ID], eax
    lea eax, [ebp+RACE_ID]
    push eax
    lea eax, [ebp+RACE_VECTOR]
    call VEC_PUSH_BACK
    pop ecx
    add esi, 4
    dec ecx
    jmp .next_race
.register:
    push STAT_COUNT
    sub esp, 0x10                       ; the race vector argument, passed by value
    mov eax, esp
    push eax
    lea ecx, [ebp+RACE_VECTOR]
    call VEC_COPY
    lea eax, [ebp+STAT_AMOUNTS]
    push eax
    lea eax, [ebp+STAT_IDS]
    push eax
    push dword [edi+REC_REALM]
    push dword [edi+REC_ID]
    push ebx
    lea ecx, [ebp+ENTRY]
    call REGISTER_CLASS                 ; frees the argument vector and pops 0x28 bytes
    lea esi, [ebp+RACE_VECTOR]
    call VEC_FREE
    mov eax, [edi+REC_RACE_COUNT]
    lea edi, [edi+REC_RACES+eax*4]      ; the next record
    jmp .next_class
.done:
    pop edi
    pop esi
    pop ebx
    leave
    ret 4

; Zero the availability mask of each listed object.
; In: ebx = registry, esi = 0-terminated byte list of ids, edi = REG_RACES or REG_CLASSES,
; edx = the mask's offset in the object. An id past the table's end or without an object is
; skipped. Changes eax, ecx and esi.
hide:
    movzx eax, byte [esi]
    inc esi
    test eax, eax
    jz .done
    mov ecx, [ebx+edi+4]
    sub ecx, [ebx+edi]
    shr ecx, 2                          ; the number of table entries
    cmp eax, ecx
    jae hide
    mov ecx, [ebx+edi]
    mov ecx, [ecx+eax*4]
    test ecx, ecx
    jz hide
    and dword [ecx+edx], 0
    jmp hide
.done:
    ret

align 4, db 0
; base_table (records, then dd 0), hide_classes and hide_races (bytes, then 0), descriptions
%include "baseclass_data.inc"
