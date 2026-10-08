# Sub-project 4: the Guild of Shadows epic chain, levels 7–50 (design)

Status: the design sections were approved by the owner in conversation on 2026-10-08; this written spec awaits the
owner's review. The decisions in section 1 are the owner's. Date: 2026-10-08.
Fork: `lometur/HearthDAoC`. Issue: #73. Branch: `sub4-shadows-epic`. Release: merging the PR publishes the next
release; the server picks it up with `./hdc update`. The PR is opened on `lometur/HearthDAoC` only (with
`gh api -X POST repos/lometur/HearthDAoC/pulls`, because `gh pr create` fails on this repo); nothing is posted to
`shadowofze/OfflineDAoC` or OpenDAoC.

File references are to HearthDAoC `main` at 8898583 (`source/server/GameServer/` unless a path says otherwise) and
to the clean classic world (`clean-classic-0.34.db`). Coordinates are zone-local, as `/loc` shows them.

## 1. Goal

Give the Guild of Shadows its epic chain as it was played in the Shrouded Isles era: a quest from the class trainer
at level 7, then steps at 11, 15, 20, 25, 30 and 40, and the four-part finale "Lord of Deceit" at 43, 45, 48 and 50,
each step needing the one before. Today the server has only a level-50 quest for these classes, and it is the wrong
one: `scripts/quests/Albion/epic/Shadows50.cs` is a copy of the Defenders of Albion quest ("Feast of the Decadent")
with the class list changed.

The chain is built on a small reusable framework, so later guild lines (Academy, Church, Defenders, and the
Midgard and Hibernia lines) mostly add data rather than new engines.

### Decisions (owner, 2026-10-07 and 2026-10-08)

| Topic | Decision |
|---|---|
| Scope | Fix the level-50 epics (the real "Lord of Deceit" for the Guild of Shadows, and the duplicate-NPC lookups), then build the whole Guild of Shadows line 7→50 for Infiltrator, Mercenary, Cabalist, Necromancer and Reaver. Heretic is out. The other guild lines are later sub-projects. |
| Approach | A fork-owned chain framework (a rules class with no server dependencies, plus game wiring), with the Guild of Shadows written as a definitions table. Special mechanics get small custom hooks. |
| Dialogue | Faithful and marked: the original steps, places, rewards and surviving lines (journal texts included). Missing lines are written in their style, and everything written new is listed in a ledger. |
| Gating | Strict order, forgiving. Each step needs the previous one and at least the step's level. No upper level limit. A declined step is offered again, and a lost quest item is replaced. |
| Shrouded Isles steps | By place. The Caer Gothwaite trainers offer the SI versions of 7 and 11 (Strange Beings, Shades and Shadows) to any race; the Camelot trainers offer the classic versions. Taking one closes the other. Either one unlocks the next step. |
| Givers | Both Camelot trainers of each class and the class's Caer Gothwaite trainer; trainers in other towns give nothing. Captain Rhodri gives 20, 25, 30 and 50. |
| XP | By the step's own level: a quarter of a level for the 7 and 11 steps (both versions), a tenth of a level from 15 on, none at 50. A character doing the 7 step at 40 gets a quarter of level 7. |
| Coin | The amounts the sources give (7 SI: 7 silver; 11 SI: 6 silver twice; 43: none). Every other step pays its level in silver, a reconstruction marked in the ledger. |
| Existing characters | They start at step 7 like everyone else. A finished copy of the old `Shadows_50` quest counts as the finished 50 step, so nobody gets a second armour set. |
| Testing | CI tests for the rules and the data, Python world tests, and an in-game test guide with GM commands (levels, teleports, items, a GM-only `/epic` command) so the owner can test each step quickly. |
| Upstream | Nothing is sent upstream: no PRs, issues or comments on `OfflineDAoC` or OpenDAoC for now. Once the chain plays well, upstream candidates are noted on the fork's own tracker #49 only. |

### Non-goals

- The other guild lines (Academy, Church of Albion, Defenders of Albion, and all Midgard and Hibernia lines). Their
  level-50 quests stay as they are, apart from the lookup fix in section 5.7.
- Heretic (class 33, disabled on this server) and the Heretic-era rewards (Blood Encrusted Flail).
- "Kiss of Death", a Reaver level-40 weapon named in the sources but with no stats anywhere.
- The live Supply Run's first version (Lundeg Tranyth, the escort by Lady Aelawen, Guard Blaen), the older bwgan-hunter
  proof at 25, and the disputed translation trip at 43.
- The XP events on entering and leaving Tepok's Mine at 40, and the trainer visit after the armour at 50 (it paid XP
  only): each step pays one XP share at its end (section 4.5), and 50 pays none.
- The live rule that the Arawnite Assassin doesn't appear for a player already wearing the level-30 reward.
- A "?" marker over NPCs that end a step. It would need changes to upstream NPC classes. The "!" marker over an NPC
  that offers a step works.
- The patch 1.79 rule that characters created after it don't get the old chain; this server is earlier than 1.79.

## 2. What players see

### 2.1 Who gives the steps

The chain is for Infiltrators, Mercenaries, Cabalists, Necromancers and Reavers, after promotion at 5. All five do
the same steps; only the rewards differ (section 4).

| Steps | Given by |
|---|---|
| 7, 11 (classic) | either of the class's two trainers in Camelot (table below) |
| 7 SI, 11 SI | the class's trainer in Caer Gothwaite |
| 15, 40, 43, 45, 48 | the class's givers: either Camelot trainer or the Caer Gothwaite trainer |
| 20, 25, 30, 50 | Captain Rhodri, Snowdonia Fortress |

| Class | Camelot trainers | Caer Gothwaite trainer |
|---|---|---|
| Infiltrator | Master Eadig, Master Edric | Elaru |
| Mercenary | Master Arenis, Master Almund | Master Rhinear |
| Cabalist | Magus Agyfen, Magus Isen | Magess Sharifa |
| Necromancer | Tatia, Yulia | Carys |
| Reaver | Charone, Peze | Melarlian |

A trainer offers steps only to its own class. Trainers of these classes in other towns (Aldland, Dales of Devwy,
Black Mountains North and others) don't give steps; the sources name only the Camelot and Caer Gothwaite trainers.

### 2.2 Taking and playing a step

- **Offers.** A "!" shows over an NPC when it has a step ready for you. Right-click it and the NPC says the offer's
  first line. If the offer has keywords, each one (in order) gives the next line; the yes/no window opens after the
  last keyword, or on the right-click itself when there are none. Saying no ends the offer; it starts again from the
  first line next time.
- **Order.** A step needs the previous step finished and your level at least the step's level; there is no upper
  limit. The order is 7 or 7 SI → 11 or 11 SI → 15 → 20 → 25 → 30 → 40 → 43 → 45 → 48 → 50. One version of 7 (or of
  11) closes the other version of that level while it is active or finished.
- **Doing it.** You talk with [keywords] (click the word or whisper it) and hand items over by dragging them onto the
  NPC. Within a stage, keywords and hand-ins go in the order the stage lists; one said out of order gets the current
  prompt again. Progress is saved as you go; logging out or a server restart loses nothing.
- **Kills.** A kill counts for you when the game gives you credit for it: you or your pet or `/spawn` companion
  damaged it, or you are within kill-credit range of it and a member of your group (or their pet or companion) damaged
  it. One kill counts once.
- **Lost items.** A quest item is needed from the stage that produces it until the stage that takes it. While you
  need one and don't have it (worn, in the backpack or in your vault), the NPC that gives it gives a new one when you
  speak to them, and the next kill of the monster that drops it gives a new one; your stage doesn't change. Nobody
  gets more copies than the step uses (two Unworked Stones at 11; one of every other item).
- **Full bags.** An NPC that gives a quest item or a reward says so and waits until you have a free slot (six for the
  armour at 50). Only an item from a kill can fall to the ground when your bag is full.
- **Dropping a step.** If you drop a step from the journal, its quest items go with it, and the giver offers it again
  from the start. A GM can also clear a character's chain with `/epic reset`.
- **Changing class.** An active step moves on only for the five classes; the reward is the current class's.

### 2.3 Rewards, XP and coin

- A class item at every step except 48 (section 4.2).
- At 40, Rhodri names your class's weapons as keywords and you pick one. Once, later, you can hand it back to him and
  pick another (section 3.3, level 40).
- At 50, the six-piece armour.
- XP: a quarter of a level at 7 and 11, a tenth of a level from 15 on, none at 50 (section 4.5).
- Coin: section 4.5.

### 2.4 Existing characters

- A character with a finished `Shadows_50` (the Defenders copy) keeps it as the finished 50 step. They do 7 to 48
  for the other rewards; the 50 step is already done, so there is no second armour set.
- A character in the middle of `Shadows_50` loses that journal entry (the quest no longer exists) and takes the real
  chain. A sealed pouch from it, if any, stays in the bag and does nothing.
- Everyone else starts at 7.

## 3. The chain step by step

### 3.1 NPCs the chain uses

All of these stand in the clean world. The code finds each one by name and region at startup (section 5.4).

| NPC (name in the world) | Role | Zone | Region | Loc |
|---|---|---|---|---|
| the 15 trainers in section 2.1 | givers | City of Camelot / Isle of Glass | 10 / 51 | guild cluster ~23–25k,18–20k; Yulia and Peze ~31k,20.7k; Caer Gothwaite ~41–44k,57–59k |
| Bline Tengit | 7: gives the supplies | Black Mtns. South | 1 | 24253,43202 |
| Thol Dunnin | 7: sends you on | Black Mtns. North | 1 | 39758,40048 |
| Ley Manton (the `GameMerchant` row) | 7: takes the supplies | Black Mtns. North (Swanton Keep) | 1 | 28147,12269 |
| Tage | 7 SI, 11 SI | Isle of Glass | 51 | 41143,57964 |
| Kimmy Glaze | 7 SI | Isle of Glass | 51 | 37947,20150 |
| Adam Glaze | 7 SI, 11 SI | Isle of Glass | 51 | 43229,55708 |
| Captain Dillon | 11 | Black Mtns. South | 1 | 52858,56604 |
| Omis | 11, 20 | Black Mtns. North (Swanton Keep) | 1 | 27005,13205 |
| Captain Rhodri | 15–50 | Snowdonia (Snowdonia Fortress) | 1 | 28527,56714, Z 9088 |
| Elrigh | 15 | Black Mtns. North (Snowdonia Station) | 1 | 40584,39781 |
| Andri | 15 | City of Camelot | 10 | 23657,20566 |
| Twr ap Alsig | 25, 40, 43 | Llyn Barfog | 1 | 35425,31698 |
| Scribe Veral | 30 | City of Camelot | 10 | 27116,13684 |
| Lieutenant Kuebler | 40, 45 | Forest Sauvage (Castle Sauvage) | 1 | 19939,58152 |
| Sir Tilian | 48 | Forest Sauvage (Castle Sauvage) | 1 | 21034,58537 |

### 3.2 Kill targets

World targets are matched by name (ignoring case) and region. Encounter monsters (section 3.4) are credited by their
own death, not by name.

| Target (name in the world) | Step | Source of the monster | Zone, loc |
|---|---|---|---|
| lesser water elemental | 7 SI | world (35 live) | Isle of Glass, around 56.5k,36.5k |
| Frund | 11 | world; moved by the world fix | Black Mtns. South 54120,17650, in the red-dwarf camp beside Agisthil |
| Agisthil | 11 | world; restored | Black Mtns. South 54014,17538 |
| Brodic | 11 | world; restored | Black Mtns. South 37126,10154 |
| Martley | 11 SI | world (live, respawns), plus an encounter copy (section 3.4) | Isle of Glass 5299,31748 |
| drakoran neophyte | 11 SI | world (20 live) | Isle of Glass 4.6–5.4k,27.7–28.9k and 9.1–10.5k,40.6–42.7k |
| renegade guard | 20 | world (9 live) | Llyn Barfog 59.3–60.9k,27.6–29.7k |
| arawnite messenger | 20 | world; restored | Black Mtns. North 4757,39084 |
| Sir Dillus | 25 | world; restored (with Lieutenant Grimarth) | Llyn Barfog 60723,28445 |
| cythraul or wicked cythraul (not "Llyn Cythraul") | 25 | world (8 and 3 live) | Llyn Barfog, south of the merchant house (~33–34k,53–54k) and 19–21k,51–54k |
| Arawnite Assassin | 30 | encounter | Snowdonia Fortress (section 3.4) |
| Savant | 40 | world (live) | Tepok's Mine 28819,22141 |
| Overseer Tepok | 43 | world (live) | Tepok's Mine 27276,26764 |
| Delfina | 43 | encounter (new) | Pennine Mountains 7657,57993, in the restored Tylwyth Teg camp |
| Isolationist Courier | 45 | encounter | Pennine Mountains 36600,56600, in the restored isolationist camp |
| Belgrik | 45 | encounter | Pennine Mountains 4000,38000, at the west edge of the ravenclan giants |
| Ellyll Seer | 48 | encounter | Pennine Mountains 6646,49018, a hut of the restored Ellyll village |
| Lord Elidyn | 50 | encounter | Pennine Mountains 2910,52462, Z 5032, the Ellyll ruins |

How the spots were chosen (each is a ruling in the ledger; the old-frontier locations in the sources disagree by up
to about 11,000 units, and the owner confirms each in game):

- **Archived row** where one exists (Agisthil, Brodic, the messenger, Sir Dillus, Lord Elidyn).
- **Frund:** a free spot in the red-dwarf camp, between its thieves, about 150 units from Agisthil (Z from the camp's
  rows; heading 1024).
- **Delfina:** Rhodri's surviving line sends you to "infiltrate a camp of Tylwyth Teg elves. There is one among them
  named Delfina", so she stands in the restored ranger camp. Uthgard's 7657,57993 lies inside it; the 2003 guide's
  "about 10000, 60000" is about 3,000 units away.
- **Belgrik:** bdo 2004's 4000,38000, the one source that falls among the ravenclan giants still in the world (the
  2002 guide: "W of Berk ... in a tree-filled valley").
- **Courier:** Uthgard's 36600,56600, between bdo's and Warcry's spots and inside the restored camp.
- **Seer:** Uthgard's 6646,49018, which agrees with the 2003 guide's "6.5k, 50k".

### 3.3 The steps

Each step is one quest with numbered stages; the journal shows the stage's text. In the stage tables, "talk" means
right-click, "say" means a keyword, "give" means dragging the item onto the NPC, and kills follow section 2.2. Tags:
**[V]** verbatim from a source (spelling slips that are plainly the transcriber's, such as "betryal", are corrected
and the correction noted in the ledger; the live game's own quirks in journal texts and NPC lines, such as "Travels",
"Sir Bor's" and Veral's "not see", are kept), **[R]** written for
this server (ledger, section 8). `<class>`, `<race>` and `<name>` are filled from the player. Quest items are in
*italics* (section 3.5).

#### Level 7: "Traveler's Way -- Supply Run" (classic; the Camelot trainers)

| # | Journal | What moves it on | What happens |
|---|---|---|---|
| offer | | talk to the trainer | One offer line [R]; the yes/no window. |
| 1 | Collect supplies from Bline Tengit, a merchant in a hut in Humberton, Black Mountains South. [R] | talk to Bline Tengit | He gives you a *Sack of Supplies*. [R] |
| 2 | Take the supplies to Thol Dunnin, the smith at Snowdonia Station. [R] | talk to Thol Dunnin | He says they are not for him and sends you to Ley Manton. [R] |
| 3 | Give the supplies to Ley Manton in Swanton Keep. [R] | give the sack to Ley Manton | Reward, XP, 7 silver; step done. |

#### Level 7 SI: "Strange Beings" (the Caer Gothwaite trainer)

| # | Journal | What moves it on | What happens |
|---|---|---|---|
| offer | | talk to the trainer | One offer line [R]; the yes/no window. |
| 1 | Speak with Tage in Caer Gothwaite. [R] | talk to Tage | Tage sends you to Kimmy Glaze. [R] |
| 2 | Find Kimmy Glaze on the Isle of Glass. [R] | talk to Kimmy Glaze | She asks you to kill two lesser water elementals. [R] |
| 3 | Slay two lesser water elementals near the lake. [R] (counter 0/2) | kill lesser water elemental ×2 | |
| 4 | Return to Adam Glaze at the gate of Caer Gothwaite. [R] | talk to Adam Glaze | Reward, XP, 7 silver; step done. |

#### Level 11: "Entry Into Tomorrow" (classic; the Camelot trainers)

| # | Journal | What moves it on | What happens |
|---|---|---|---|
| offer | | talk to the trainer | One offer line [R]; the yes/no window. |
| 1 | Seek out Captain Dillon in the guard tower south-east of Ludlow. [R] | talk to Captain Dillon, say [your help] (a keyword guessed by a player, kept) | He tells of Brodic, the Crediac stone and the runaway guards Frund and Agisthil. [R] |
| 2 | Slay the runaway guards Frund and Agisthil at the red-dwarf camp east of the bridge. [R] | kill Frund and kill Agisthil, in either order | Each kill gives an *Unworked Stone* (two in all). |
| 3 | Take the stones to Omis, the gemcutter in Swanton Keep. [R] | give an Unworked Stone to Omis | He takes every stone you carry, cuts one into the *Crediac* and scorns the other [R, after a player's paraphrase: "do you think me a fool?"]. A player at this stage with no stone gets one from the next Frund or Agisthil kill. |
| 4 | Hunt Brodic north of the bridge, carrying the Crediac. [R] | kill Brodic while carrying the Crediac | Near Brodic: "The pouch holding the Crediac glows with warmth and light." [V]. On his death the Crediac shatters (it is removed). |
| 5 | Return to Captain Dillon. [R] | talk to Captain Dillon | Reward, XP, 11 silver; step done. |

#### Level 11 SI: "Shades and Shadows" (the Caer Gothwaite trainer)

| # | Journal | What moves it on | What happens |
|---|---|---|---|
| offer | | talk to the trainer | One offer line [R]; the yes/no window. |
| 1 | Speak with Adam Glaze at the gate of Caer Gothwaite. [R] | talk to Adam Glaze | He gives you *Martley's Cloak*. |
| 2 | Wear Martley's cloak and find Martley among the drakoran on the Isle of Glass. [R] | Martley is killed (section 3.4) while you wear the cloak | *Martley's Body* goes into your bag. |
| 3 | Bring Martley's body to Adam Glaze. [R] | give the body to Adam Glaze | He takes the body and the cloak (worn or not), pays 6 silver and gives you *Adam Glaze's Letter*. |
| 4 | Take Adam's letter to Tage. [R] | give the letter to Tage | Reward, XP, 6 silver; step done. |

#### Level 15: "Rebellion Accepted" (the class's givers)

| # | Journal | What moves it on | What happens |
|---|---|---|---|
| offer | | talk to the trainer | One offer line [R]; the yes/no window. |
| 1 | Seek out Captain Rhodri at the Snowdonia Border Keep in Snowdonia Fortress. [V] | talk to Captain Rhodri | He sends you to Elrigh. [R] |
| 2 | Seek out Elrigh. It is said he Travels from inn to inn within the Black Mountains. Mention to him your interest of the [locals] within Barfog. [V] | say [locals] to Elrigh | He sends you to Andri. [R] |
| 3 | Locate Andri inside of Camelot. He frequents the Bars there. Speak to him of [Barfog] and the [Renegade] guards. [V] | say [Renegade], then [Barfog], to Andri (Andri "begins talking" on renegade, per a player) | Andri tells of the renegade guards at Barfog and of Sir Dillus. [R] |
| 4 | Return to Captain Rhodri at Snowdonia Border keep in Snowdonia Fortress. Inform him of the [Renegade] and of [Sir Dillus]. [V] | say [Renegade], then [Sir Dillus], to Rhodri | Reward, XP, 15 silver; he tells you to return at 20 [R]. Step done. |

#### Level 20: "Path of the Renegade" (Captain Rhodri)

| # | Journal | What moves it on | What happens |
|---|---|---|---|
| offer | | talk to Rhodri at 20 | "We cannot actively move on the Renegades without a reason. So we have decided to send you to their tower under the Cover of Darkness. There you will subdue one of the Renegade guards and learn all you can. Do not spend much time in the Vicinity. The moment you learn of something, return to us. Farewell <class>." [V]; the yes/no window. |
| 1 | Slay a Renegade Guard in Llyn Barfog. [V] | kill a renegade guard | "<name> notices a book on the guard's belt." [V]; you get the *Renegade Diary*. |
| 2 | Return to Captain Rhodri with the Diary you have obtained. [V] | give the diary to Rhodri | He sends you after the messenger. [R] |
| 3 | You must move quickly and locate the Arawnite Messenger in the Pass that leads to Llyn Barfog. [V] | kill the arawnite messenger | You get the *Arawnite Orders*. |
| 4 | Return to Captain Rhodri with the orders you have taken from the Arawnite messenger. [V] | give the orders to Rhodri | He can't read them and gives them back [R]. |
| 5 | A vendor by the name of Omis may be able to decipher the Arawnite orders. He can be found in the Keep just south of Snowdonia. [V] | give the orders to Omis | He reads them and puts notes on the paper [R]: you get the *Annotated Arawnite Orders*. |
| 6 | Return to Captain Rhodri at the Snowdonia Border Keep. Give him the Arawnite Orders. [V] | give them to Rhodri | Reward, XP, 20 silver; "You have finished the Path of the Renegade quest." [V]. Step done. |

#### Level 25: "Regal Nobility" (Captain Rhodri)

| # | Journal | What moves it on | What happens |
|---|---|---|---|
| offer | | talk to Rhodri at 25; say [matter], then [dispatch] | Rhodri's three lines [V] ("<name>, it is good to see you are still alive ... [matter] ... [dispatch] the outcasts ... Take with you the Arawnite orders as proof of Sir Dillus' betrayal."); the yes/no window; on accepting, "You receive Arawnite orders from Captain Rhodri!" [V] (a new *Arawnite Orders*). |
| 1 | Slay Sir Dillus and his renegade officers! [V] | kill Sir Dillus (only he is required, as players reported) | |
| 2 | Speak to Twr ap Alsig at the local outpost in Llyn Barfog. Mention Sir Bor's wish to lend them [support]. Also mention [Sir Dillus]' betrayal! [V] | talk to Twr ("Yes <race>? What do you want?" [V]); say [support]; say [Sir Dillus]; give the orders; say [certain] | Twr's lines [V], one after each ("Support you say? ...", "We know you have killed him! ...", "Heavens! ... I must be [certain] where your loyalties lay.", "There roams about this land the undead cythraul ..."). |
| 3 | Slay a cythraul within Llyn Barfog! [V] | kill a cythraul or wicked cythraul | You get a *Cythraul Skull*. |
| 4 | Speak to Twr ap Alsig at the local outpost in Llyn Barfog. [V] | talk to Twr ("I still seek proof of your deeds. Do you have something for me?" [V]); give the skull | "You receive a <reward> from Twr ap Alsig!" [V]; "You have done well <race>! We shall support your cause and welcome you always. Please take this as a token of trust between our people." [V]; XP, 25 silver; "You have finished the Regal Nobility quest." [V]. Step done. |

#### Level 30: "Rebellion Accepted" (Captain Rhodri)

| # | Journal | What moves it on | What happens |
|---|---|---|---|
| offer | | talk to Rhodri at 30 | "Ah <name>! You have returned and just in time as well. ..." [V]; the yes/no window; on accepting you get the *Document for Scribe Veral* ("You recieve Document for Scribe Veral" is shown as "You receive ..."). |
| 1 | Travel to Camelot and speak with Scribe Veral. Hand to him the documents from Captain Rhodri. [V] | talk to Veral ("Hail traveler! How may I be of service to you?" [V]); give the document; say [local report] | "You are from Sir Bors? How can it be you have not see the [local report] yet?" [V, the NPC's own slip]; then the assassination warning and King Constantine [V]. |
| 2 | Return to the Snowdonia border keep. Search the keep for the assassin and slay him! [V] | the Arawnite Assassin is killed (section 3.4) | His lines on appearing and dying [V]. |
| 3 | Speak to Captain Rhodri at the Snowdonia border keep. [V] | talk to Rhodri | "<name>! You have done us the greatest of services! ... we shall once again call upon the Guild of Shadows and their most cunning <class>!" [V; "the Guild of Shadows" stands for the source's "(prof name)" and is R]; 30 silver ("You are awarded some cash!" [V]), XP, reward; "You have finished the Rebellion Accepted quest." [V]. Step done. |

#### Level 40: "Hidden Insurrection" (the class's givers)

| # | Journal | What moves it on | What happens |
|---|---|---|---|
| offer | | talk to the trainer; say [interested], then [doubts] | The trainer has a task, speaks of trouble in Snowdonia, has [doubts], and sends you there [R, after the 1.52 walkthrough's account]; the yes/no window. |
| 1 | Seek out Captain Rhodri at the Snowdonia Border keep. [V] | say [down to business], [Arawnites], [speak with Lieutenant Kuebler] to Rhodri | His lines [R]; you get *Rhodri's Letter*. |
| 2 | Deliver Rhodri's letter to Lieutenant Kuebler. He can be found at Castle Sauvage on second floor from Rhodri. [V] | give the letter to Kuebler | He calls you a lackey and sends you back to Snowdonia [R]. |
| 3 | Return to Captain Rhodri at the Snowdonia border keep. [V] | talk to Rhodri, say [Twr ap Alsig] | He grumbles and gives you *Sir Bors' Chest* [R]. |
| 4 | Speak to Twr ap Alsig of Barfog about the [goblin] uprising. Deliver to him Sir Bor's chest filled with riches. [V] | give the chest to Twr; say [goblin], [outside influence], [link to the borderlands] | Twr speaks of an outside influence and a tunnel in Tepok's Mine, and points you to a Savant [R]. |
| 5 | Locate the mine controlled by goblins within the Black Mountains. Seek out a Savant within the mine and learn what you can of the passageways. You may be forced to slay the goblin and search his belongings. [V] | kill the Savant | His death line "My pets shall put you in your place!" [V]; you get the *Dwarven Dispatch*. |
| 6 | The dispatch speaks of an attack to be carried out upon the locals within Barfog! Carry this dispatch to Twr ap Alsig within Barfog to warn him of this impending danger. [V] | give the dispatch to Twr | He writes the *Document for Sir Bors* [R]. |
| 7 | Return to Captain Rhodri at the Snowdonia border keep. [V] | give the document to Rhodri, then say the name of a weapon | "This is a great day <name>! ... What reward do you desire? [Death's Touch], a thrusting weapon? ..." [V for the Infiltrator; the same speech with each class's weapons, R, for the others]; the weapon you name is your reward, with XP and 40 silver. Step done. |

The swap: after the step, hand the weapon to Rhodri (no keyword, as on live). He keeps it, lists the weapons again,
and the character is marked "swap pending"; each later talk lists them again. Naming a weapon gives it (when there
is a free slot) and marks "swap used"; after that he refuses weapons. Both marks are saved on the finished step. A
sold or destroyed weapon is not replaced.

#### Level 43: "Lord of Deceit" (the class's givers)

| # | Journal | What moves it on | What happens |
|---|---|---|---|
| offer | | talk to the trainer; say [interested] | "Hail! I trust you are well? I have a mission for you should you be [interested]." [V]; then "Many in our order have begun to speak at length of your missions for Sir Bors. ... Safe travels!" [V]; the yes/no window. |
| 1 | Seek out Captain Rhodri at the Snowdonia Border Keep in Snowdonia Fortress. [V] | talk to Rhodri; say [assist us] | Rhodri's lines [V], ending with the order to slay Tepok and bring back his sack. |
| 2 | Descend into Tepok's Mine, slay Overseer Tepok and bring back his bag. [R] | kill Overseer Tepok | You get *Tepok's Bag*. |
| 3 | Return to Captain Rhodri with Tepok's bag. [R] | talk to Rhodri ("Ah! Were you able to retrieve Tepok's precious bag?" [V]); give the bag; say [Twr ap Alsig] | "This is what we have been looking for! [Twr ap Alsig] has been deceived. ..." [V]; on [Twr ap Alsig] he gives you *Tepok's Dispatch* and sends you to Twr [R]. |
| 4 | Deliver dispatch to Twr ap Alsig. [V] | give the dispatch to Twr | Twr's lines [R]. |
| 5 | Return to Captain Rhodri. [V] | talk to Rhodri ("Were you able to deliver the dispatch to Twr ap Alsig?" [V]); say [work is done], [scouts were spotted], [venture into the camps], [willing to assist] | Rhodri's lines [V], ending with the order to find Delfina in the Tylwyth Teg camp. |
| 6 | Slay Delfina in the camp of the Tylwyth Teg in the Pennine Mountains, and return her reports. [R] | Delfina is killed (section 3.4) | You get *Delfina's Reports*. |
| 7 | Return to Captain Rhodri with Delfina's reports. [R] | talk to Rhodri ("Once again your swiftness is most impressive! Do you have Delfina's reports?" [V]); give the reports | "This does not bode well. ... Return to me later and perhaps I will have news. Good day to you!" [V]; reward (ring), XP, no coin. Step done. |

#### Level 45: "Lord of Deceit" (the class's givers)

| # | Journal | What moves it on | What happens |
|---|---|---|---|
| offer | | talk to the trainer | One offer line [R]; the yes/no window. |
| 1 | Seek out Captain Rhodri at the Snowdonia Border Keep in Snowdonia Fortress. [V, as at 43] | talk to Rhodri | He sends you after the isolationist courier [R]. |
| 2 | Slay the isolationist courier in his camp in the Pennine Mountains and take his documents. [R] | the Isolationist Courier is killed (section 3.4) | You get the *Isolationist Documents*. |
| 3 | Bring the documents to Captain Rhodri. [R] | give them to Rhodri | He sends you to Kuebler about the giants' book [R]. |
| 4 | Ask Lieutenant Kuebler in Castle Sauvage about the [book of lore]. [R] | say [book of lore] to Kuebler | He sends you after Belgrik [R]. |
| 5 | Slay Belgrik of the Ravenclan giants in the Pennine Mountains and take his Book of Lore. [R] | Belgrik is killed (section 3.4) | You get the *Book of Lore*. |
| 6 | Return the Book of Lore to Captain Rhodri. [R] | give it to Rhodri | He sends you to your trainer [R]. |
| 7 | Return to your trainer. [R] | talk to any of your class's givers (section 2.1) | Reward (cloak), XP, 45 silver; step done. |

#### Level 48: "Lord of Deceit" (the class's givers)

| # | Journal | What moves it on | What happens |
|---|---|---|---|
| offer | | talk to the trainer | One offer line [R]; the yes/no window. |
| 1 | Seek out Sir Tilian upstairs in Castle Sauvage. [R] | talk to Sir Tilian | He sends you after an Ellyll seer [R]. |
| 2 | Slay an Ellyll seer in the Ellyll village in the Pennine Mountains, and take its journal. [R] | the Ellyll Seer is killed (section 3.4) | You get the *Seer's Journal*. |
| 3 | Bring the journal to Sir Tilian. [R] | give it to Sir Tilian | He annotates it: *Annotated Seer's Journal* [R]. |
| 4 | Take the annotated journal to Captain Rhodri. [R] | give it to Rhodri | XP and 48 silver, no item. He tells you to come back at 50 [R], or, if your 50 step is already finished (section 2.4), thanks you without a summons [R]. Step done. |

#### Level 50: "Lord of Deceit" (Captain Rhodri)

| # | Journal | What moves it on | What happens |
|---|---|---|---|
| offer | | talk to Rhodri at 50 | One offer line [R]; the yes/no window. |
| 1 | Slay Lord Elidyn, the Lord of Deceit, in the Ellyll ruins on the hill in the Pennine Mountains. [R] | Lord Elidyn is killed while you are alive (section 3.4) | |
| 2 | Return to Captain Rhodri. [R] | talk to Rhodri with six free bag slots | Without six free slots he says so and waits. With them: the six armour pieces and 50 silver; step done. |

### 3.4 Encounters

Encounter monsters are made by the script and never saved to the world. Each is a new NPC object with
`LoadedFromScript` set, no `InternalID`, its own death handler attached at creation, and no respawn timer of its own.
Its name is set in title case after its template loads (Arawnite Assassin, Martley, Delfina, Isolationist Courier,
Belgrik, Ellyll Seer, Lord Elidyn, Ellyll Froglord, Ellyl Hero), so the autonomous bots' grind catalogue, which picks
lower-case names, leaves them alone (as with the Sluaghbinder encounters).

General rules, unless the table says otherwise:

- **Who needs it:** a player on the stage that kills it, or on a later stage of the same step who has lost the item it
  drops (section 2.2).
- **Presence:** one copy stands at its spot while a player who needs it is within 3,000 units; it goes away 5 minutes
  after the last such player leaves.
- **After any death:** if a player who still needs it is within 3,000 units, a new copy appears 3 minutes later.
- **Credit:** when it dies, a player who needs it gets credit if they or their pet or companion is in the monster's
  damage list (`XPGainers`) at that moment, or if they are within kill-credit range of the monster and a member of
  their group (or that member's pet or companion) is in the list. A guard or bot landing the last blow doesn't take
  the credit away.

| Encounter | Rule |
|---|---|
| Crediac glow (11) | While a player on stage 4 carries the Crediac within 1,500 units of Brodic, the message shows at most once a minute. |
| Martley (11 SI) | "A player who needs Martley" here means one on stage 2, or on stage 3 without Martley's Body, who wears Martley's Cloak. The world's Martley counts for such a player who gets kill credit for him (section 2.2). If he isn't standing at the camp (dead, waiting to respawn), such a player who gets credit for a drakoran neophyte calls up an encounter copy of Martley (level 11–12, with two drakoran neophytes) beside the neophyte's body. The copy follows the general presence and credit rules; it doesn't come back after a death (the world's Martley respawns, and a copy can be called up again). |
| Arawnite Assassin (30) | One per triggering player at a time, and only his own rules apply (not the general presence and after-death rules). He appears at the player's spot after a player on stage 2, not stealthed, has stayed for a random 30–90 seconds within 200 units of one spot inside the world's "Snowdonia Fortress" area (region 1, centre 28739,55186, radius 3,500); moving farther starts the wait again. Template 60157994 (level 32–33). He attacks that player and says his lines. He goes away 2 minutes after that player dies, logs out or leaves the area; if he dies or goes away without giving credit, the wait can start again. The fortress's own NPCs may join the fight: the credit rule keeps the kill for the player's group as long as it damaged him. The Grab Bag (2004) advises the landings behind the small doors in the courtyard's east and west walls, where guards don't interfere; the test guide sends the owner there, and `/epic goto` uses the landing inside the door by Rhodri's tower (door 12000101, 28743,56885, Z 8320) until the owner records the wall landings. |
| Delfina (43) | Made in code: named Delfina, level 45, with the model and gear of the Tylwyth Teg huntress template (60167341), and two Tylwyth Teg rangers beside her. |
| Isolationist Courier (45) | Template 60162534 ("isolationist courier", level 46), in the camp. |
| Belgrik (45) | Template 60158349 (level 51), among the ravenclan giants. |
| Ellyll Seer (48) | Template 60160431 ("ellyll seer", level 59), in the hut. |
| Lord Elidyn (50) | Template 60163397 (level 59, model 74, size 26) at his archived spot, among the restored ruin guards (14 Ellyll guards, levels 49–51, section 6) and with four ellyll froglords (template 13006) around him. At 75%, 50% and 25% of his health he cries out [R, adapted from patch 1.48's "The scout claims Lord Elidyn's power can change frogs into Ellyll heroes!"] and turns one living froglord into an Ellyl Hero (template 90000, levels 57–60). Credit only for players who are alive when he dies. |

### 3.5 Quest items

All are new templates in code (section 5.5): not tradable, not droppable, no value, Realm 0. Martley's Cloak is a
cloak with no bonuses (it must be worn); the rest are plain inventory items. A name is tagged [V] only where a game
message, patch note or item text gives it exactly; the others are [R], with the wording they come from.

| Item | Step | From | To |
|---|---|---|---|
| Sack of Supplies [R] | 7 | Bline Tengit | Ley Manton |
| Martley's Cloak [V, patch 1.70] | 11 SI | Adam Glaze | taken back by Adam Glaze |
| Martley's Body [R, the page's "Martley's body"] | 11 SI | Martley | Adam Glaze |
| Adam Glaze's Letter [R; the source says "something for Tage"] | 11 SI | Adam Glaze | Tage |
| Unworked Stone [R, the page's "unworked stone"] | 11 | Frund, Agisthil | Omis |
| Crediac [V, the glow message] | 11 | Omis | shatters on Brodic's death |
| Renegade Diary [R; the source says "the Diary"] | 20 | a renegade guard | Rhodri |
| Arawnite Orders [V, "You receive Arawnite orders"] | 20, 25 | the messenger (20); Rhodri on accepting (25) | Omis (20); Twr ap Alsig (25) |
| Annotated Arawnite Orders [R] | 20 | Omis | Rhodri |
| Cythraul Skull [R, the walkthrough's "Cythraul skull"] | 25 | a cythraul | Twr ap Alsig |
| Document for Scribe Veral [V, "You recieve Document for Scribe Veral"] | 30 | Rhodri | Scribe Veral |
| Rhodri's Letter [R, the journal's "Rhodri's letter"] | 40 | Rhodri | Kuebler |
| Sir Bors' Chest [R, the journal's "Sir Bor's chest"] | 40 | Rhodri | Twr ap Alsig |
| Dwarven Dispatch [R, the walkthrough's "a dwarven dispatch"] | 40 | the Savant | Twr ap Alsig |
| Document for Sir Bors [R, the walkthrough's "a document for Sir Bors"] | 40 | Twr ap Alsig | Rhodri |
| Tepok's Bag [R, Rhodri's "Tepok's precious bag"] | 43 | Overseer Tepok | Rhodri |
| Tepok's Dispatch [R] | 43 | Rhodri | Twr ap Alsig |
| Delfina's Reports [R, Rhodri's "Delfina's reports"] | 43 | Delfina | Rhodri |
| Isolationist Documents [R] | 45 | the courier | Rhodri |
| Book of Lore [R, the keyword "[book of lore]"] | 45 | Belgrik | Rhodri |
| Seer's Journal [R] | 48 | the Ellyll Seer | Sir Tilian |
| Annotated Seer's Journal [R] | 48 | Sir Tilian | Rhodri |

An item from a kill goes straight into the bag of each player who gets credit and needs it (on the ground if the bag
is full). A step's quest items are removed when the step ends or is dropped, wherever they are (worn, backpack or
vault).

## 4. Rewards

### 4.1 Rules for item values

- **Era:** the world's 1.65 era: values after the 1.63 item upgrade, with Power as flat points (not the later
  "Power Pool %").
- **Source:** the Allakhazam item page for every field (bonuses, type, speed, DPS, damage type, procs and charges, item
  level). The rulings in 4.2–4.4 settle the known conflicts and gaps. bdo was partly compiled from Warcry, so the two
  count as one source when they agree.
- **Unknowns:** filled by analogy with sibling items and marked in the ledger (section 8).
- **Level:** the page's "Level to Attain", or the step's level if the page has none. Quality 100, full condition and
  durability.
- **Look:** model and colour from the page where it names them; otherwise an existing `ItemTemplate` row of the same
  kind and look. The plan lists every model.
- **Procs and charges:** existing rows of the world's `Spell` table with the same effect, damage type and value. The
  plan lists each spell id. No new spells.
- **Ids:** new templates use `hdc_gos_<name>` (rewards) and `hdc_gos_q_<name>` (quest items). The 30 level-50 rows keep
  their ids (`<Class>Epic{Helm,Vest,Arms,Gloves,Legs,Boots}`).
- **Flags:** price 0; droppable and tradable, Realm 0, like the existing epic armour. `AllowedClasses` is the one
  class, except Spark of Midnight and Crackling Impaler, which are one template each for both Infiltrator and
  Mercenary (`9;11`).

### 4.2 Rewards per class

| Lvl | Infiltrator | Mercenary | Cabalist | Necromancer | Reaver |
|---|---|---|---|---|---|
| 7 | Falconheaded Cloak Pin | Sleeves of Might | Silvered Cap of the Intuit | Bone Chip Pin | Twisted Bone Bracelet |
| 7 SI | Jewel of Grace | Bracer of Strength | Necklace of Greatness | Darkstone | Temple Ring |
| 11 | Amulet of Feline Graces | Might | Bookworm's Ring | Flayed Skin Necklace | Gem of Black Death |
| 11 SI | Quickened Absorption Stone | Lucky Pebble | Minding Ring | Arawn's Beads | Reaver's Stone |
| 15 | Gem of Shadowy Intentions | Fury | Boneshaper's Ring | Choker of Dark Deeds | Sleeves of Despair |
| 20 | Soft Doeskin Boots | Heavy Pull Short Bow | Boneshaper's Spine | Staff of Tainted Rage | Boots of the Fallen |
| 25 | Vest of the Infamous Blade | Light Chain Tunic | Spirit Threaded Cloak | Pain Threaded Cloak | Tunic of Dark Suffering |
| 30 | Shadowbinder's Mantle | Gauntlets of Blinding Speed | Visage of Death | Charred Staff of Blight | Mantle of Shadow |
| 40 | weapon of choice (4.3) | weapon of choice | staff of choice | staff of choice | weapon of choice |
| 43 | Ring of Shades | Ring of Shadowy Embers | Construct Ring | Ring of Forbidden Rites | Jewelled Skull Ring |
| 45 | Ignuixs' Portable Shadow | Cloak of the Shadowy Embers | Warm Construct Cloak | Cloak of Forgotten Curses | Cloak of Murky Secrets |
| 48 | — | — | — | — | — |
| 50 | Shadow-Woven set | Shadowy Embers set | Construct set | Forbidden Rites set | Murky Secrets set |

Bonuses (Allakhazam unless a ruling says otherwise; "Power" is flat points):

| Item | Slot | Bonuses |
|---|---|---|
| Falconheaded Cloak Pin | jewel | Con 4, Dex 3, Qui 3, Envenom +1 |
| Jewel of Grace | jewel | Str 3, Dex 3, Qui 1 |
| Amulet of Feline Graces | neck | Critical Strike +2, Dex 4, Qui 4, Body 1% |
| Quickened Absorption Stone | jewel | Str 4, Dex 4, Qui 7, Envenom +1 |
| Gem of Shadowy Intentions | jewel | Critical Strike +2, Stealth +2, Slash 1%, Hits 20 |
| Soft Doeskin Boots | leather feet, AF 44 | Stealth +3, Dex 6, Qui 3 |
| Vest of the Infamous Blade | leather torso, AF 54 | Dual Wield +4, Str 4, Qui 4 |
| Shadowbinder's Mantle | cloak | Stealth +3, Str 13, Dex 13 |
| Ring of Shades | ring | Critical Strike +3, Str 18, Dex 15, Envenom +3 |
| Ignuixs' Portable Shadow | cloak | Stealth +3, Con 18, Qui 18, Cold 6% |
| Sleeves of Might | studded arms, AF 18 | Str 4, Hits 6 |
| Bracer of Strength | wrist | Str 4 |
| Might | wrist | Str 7, Dex 4, Body 1%, Hits 12 |
| Lucky Pebble | jewel | Str 6, Dex 6, Qui 6, Spirit 1% |
| Fury | wrist | Dual Wield +2, Qui 7, Heat 1%, Hits 20 |
| Heavy Pull Short Bow | short bow, DPS 7.5, 4.1 s | Dex 13, Qui 12 |
| Light Chain Tunic | chain torso, AF 54 | Parry +3, Qui 9, Slash 2%, Hits 12 |
| Gauntlets of Blinding Speed | chain hands, AF 64 | Dual Wield +4, Str 6, Qui 6 |
| Ring of Shadowy Embers | ring | Dex 16, Qui 16, Thrust 6%, Hits 44 |
| Cloak of the Shadowy Embers | cloak | Str 16, Qui 16, Cold 6%, Hits 48 |
| Silvered Cap of the Intuit | cloth helm, AF 9 | Body +1, Dex 3, Int 6, Power 1 |
| Necklace of Greatness | neck | Int 6, Power 1, Hits 8 |
| Bookworm's Ring | ring | Matter +1, Dex 6, Int 6, Hits 16 |
| Minding Ring | ring | Dex 4, Int 7, Spirit 1%, Hits 12 |
| Boneshaper's Ring | ring | Body +2, Int 4, Crush 1%, Power 4 |
| Boneshaper's Spine | 2H staff, DPS 7.8, 4.4 s | Matter focus 18, Body focus 18, Spirit focus 22, Power 8 |
| Spirit Threaded Cloak | cloak | Spirit +3, Dex 9, Power 4 |
| Visage of Death | 2H staff, DPS 10.5, 4.0 s | Int 28, Matter focus 26, Body focus 26, Spirit focus 30 |
| Construct Ring | ring | Matter +3, Body +3, Spirit +3, Int 18 |
| Warm Construct Cloak | cloak | Dex 7, Int 7, Power 6 |
| Bone Chip Pin | jewel | Dex 3, Int 6, Body 1%, Death Servant +1 |
| Darkstone | jewel | Dex 3, Int 6, Cold 1%, Death Servant +1 |
| Flayed Skin Necklace | neck | Int 4, Heat 1%, Deathsight +2, Hits 12 |
| Arawn's Beads | neck | Int 7, Spirit 1%, Power 3, Hits 8 |
| Choker of Dark Deeds | neck | Int 4, Body 1%, Death Servant +2, Power 3 |
| Staff of Tainted Rage | 2H staff, DPS 7.8, 4.4 s | Deathsight focus 18, Painworking focus 18, Death Servant focus 22, Power 8 |
| Pain Threaded Cloak | cloak | Dex 9, Painworking +3, Power 4 |
| Charred Staff of Blight | 2H staff, DPS 10.8, 4.0 s | Int 28, Deathsight focus 26, Painworking focus 26, Death Servant focus 30 |
| Ring of Forbidden Rites | ring | Int 18, Deathsight +3, Painworking +3, Death Servant +3 |
| Cloak of Forgotten Curses | cloak | Str 15, Dex 18, Int 18, Power 6 |
| Twisted Bone Bracelet | wrist | Str 6, Con 3, Flexible +1, Soulrending +1 |
| Temple Ring | ring | Str 3, Con 4, Piety 3, Flexible +1 |
| Gem of Black Death | jewel | Con 6, Piety 6, Hits 12, Flexible +1 |
| Reaver's Stone | jewel | Shield +1, Con 6, Hits 16, Power 3 |
| Sleeves of Despair | chain arms, AF 34 | Str 7, Qui 7, Piety 7, Flexible +1 |
| Boots of the Fallen | chain feet, AF 44 | Str 4, Con 3, Soulrending +3 |
| Tunic of Dark Suffering | chain torso, AF 54 | Str 4, Piety 4, Flexible +4 |
| Mantle of Shadow | hooded cloak | Parry +3, Con 9, Piety 9, Thrust 4% |
| Jewelled Skull Ring | ring | Str 18, Con 15, Flexible +3, Soulrending +3 |
| Cloak of Murky Secrets | hooded cloak | Str 16, Piety 16, Flexible +3, Hits 48 |

Rulings on conflicts and gaps (each is also a ledger line):

| Item | Conflict or gap | Ruling |
|---|---|---|
| Jewel of Grace (Inf 7 SI) | bdo gives a different item ("Anneau de Vitesse", a ring) | Allakhazam's Jewel of Grace; bdo alone doesn't outweigh it. |
| Sleeves of Might, Bracer of Strength (Merc 7, 7 SI) | bdo gives higher values | Allakhazam. |
| Silvered Cap, Necklace of Greatness (Cab 7, 7 SI) | bdo and one 2002/2003 report give lower, pre-1.63 values | Allakhazam (the 1.63 upgrade explains the difference). |
| Soft Doeskin Boots | bdo and Warcry give Dex 4 | Allakhazam's Dex 6: bdo copies Warcry, so they count as one source, and the page carries the post-1.63 values. |
| Heavy Pull Short Bow | bdo DPS 7.4, Dex 9, Qui 6 | Allakhazam, which Warcry supports. |
| Light Chain Tunic | bdo lacks Parry | Allakhazam (Parry +3), which Warcry supports. |
| Visage of Death, Charred Staff of Blight | bdo 26/26/26 or a different focus split | Allakhazam. |
| Temple Ring | bdo's copy is garbled | Allakhazam. |
| Flayed Skin Necklace vs Arawn's Beads (Necro 11) | Allakhazam lists both on the classic step | classic = Flayed Skin Necklace, SI = Arawn's Beads (bdo's split, matching a Necromancer's report). |
| Boots of the Fallen | Allakhazam gives no armour type | chain, per bdo ("Mailles"); absorb as the other chain rewards. |
| Ignuixs' Portable Shadow | spelling | as on its item page: "Ignuixs'". |

### 4.3 Level-40 weapons

Rhodri lists these by name as keywords. Flags as in section 4.1. Full fields (speed, DPS 14.0–14.1, damage type, the
77-damage proc or 10 charges, levels of use) come from each item page, except where a ruling below fills a gap.

| Class | Choices |
|---|---|
| Infiltrator | Death's Touch (right hand, thrust); Death Dancer (right hand, slash); Spark of Midnight (left hand, slash); Crackling Impaler (left hand, thrust). Rhodri's 1.52 speech names exactly these four [V]. |
| Mercenary | Spark (right hand, thrust); Dazzle (right hand, slash); Glitter (right hand, crush); Spark of Midnight; Crackling Impaler; Arcing Bludgeoner (left hand, crush). |
| Cabalist | Staff of Eternal Lifeforce (Body focus 43, Matter 33, Spirit 33, Hits 100, 10 charges of a cold damage spell); Staff of Earth Channeling (Matter focus 43, Body 33, Spirit 33, Hits 100); Staff of Spirit Consumption (Spirit focus 43, Body 33, Matter 33, Hits 100). |
| Necromancer | Staff of Cursed Bondage (Death Servant focus 43); Staff of Clouded Vision (Deathsight focus 43); Staff of Ceaseless Agony (Painworking focus 43); each Hits 100 and the other two foci 33; damage types from Allakhazam. |
| Reaver | Blood Encrusted Whip (flexible slash); Flail of Fallen Graces (flexible crush); Sap of Lost Will (crush); Bloodletter (slash). |

Rulings:
- **Glitter:** Allakhazam (Crush +4, Dual Wield +4, no Strength); bdo's extra Str 7 stands alone.
- **Staff of Earth Channeling:** foci as on its page; Hits 100 by analogy with its siblings (the page shows 33, bdo
  100); 10 charges of an energy damage spell (Warcry's damage type; the charges by analogy with Eternal Lifeforce,
  since the page shows the 77-damage spell without a charge line).
- **Staff of Spirit Consumption:** no Allakhazam focus line; bdo's values (Spirit 43, Body 33, Matter 33, Hits 100)
  match the sibling pattern and are used; 10 charges of a spirit damage spell (bdo's type; charges by analogy).
- Kiss of Death and Blood Encrusted Flail are left out (non-goals).

### 4.4 Level-50 armour

The 30 existing rows are taken over by the code (section 5.5) with these fixes; everything else stays as the rows
are today:

| Row | Today | Fixed to | Why |
|---|---|---|---|
| MercenaryEpicVest | Name "Haurberk of the Shadowy Embers", AllowedClasses 0 | "Hauberk of the Shadowy Embers", Mercenary | typo; the only vest open to every class |
| the five vests | no charges | 3 charges of a self Shield (AF) 75 buff, 10 minutes | bdo, and Allakhazam for the Cabalist, Necromancer and Reaver vests; the same buff for the Mercenary and Infiltrator vests by analogy |
| MercenaryEpicArms | Con 15, Dex 16 | Con 16, Dex 15 | Allakhazam and bdo agree |
| InfiltratorEpicGloves | Envenom +3 | Envenom +4 | Allakhazam and bdo agree; the rest of the gloves stays |
| CabalistEpicBoots | Matter +3 | Matter +4 | Allakhazam and bdo agree |

Kept as the rows are, by ruling: Power on the robes, gloves and Reaver boots stays as flat points; the Necromancer
robe keeps Crush 4% (bdo 2004 and the row agree; the one era Allakhazam page shows 1%).

Vests already in a bag pick up the spell and the maximum from the template but keep their own charge count (0); the
world fix gives them their 3 charges (section 6, step 9).

### 4.5 XP and coin

XP is a share of the XP a character needs to go from the step's level to the next level: for level L,
`GamePlayer.GetExperienceAmountForLevel(L) − GamePlayer.GetExperienceAmountForLevel(L−1)`. It is given at the end of
the step as quest XP. The code multiplies it by the server's `xp_rate` itself and gives it without the engine's own
multipliers, so every step pays at the same rate wherever it ends (with the engine's multipliers, steps ending in
Snowdonia, an RvR zone, would use `rvr_zones_xp_rate` instead).

| Step | Share | XP (before `xp_rate`) | Coin |
|---|---|---|---|
| 7 | 1/4 of level 7 (22,000) | 5,500 | 7 silver [R] |
| 7 SI | 1/4 of level 7 | 5,500 | 7 silver (sources) |
| 11 | 1/4 of level 11 (380,000) | 95,000 | 11 silver [R] |
| 11 SI | 1/4 of level 11 | 95,000 | 6 silver from Adam Glaze at stage 3 and 6 silver from Tage at the end (sources) |
| 15 | 1/10 of level 15 (1,800,000) | 180,000 | 15 silver [R] |
| 20 | 1/10 of level 20 (12,300,000) | 1,230,000 | 20 silver [R] |
| 25 | 1/10 of level 25 (53,000,000) | 5,300,000 | 25 silver [R] |
| 30 | 1/10 of level 30 (210,000,000) | 21,000,000 | 30 silver [R; the source says "some cash"] |
| 40 | 1/10 of level 40 (4,300,000,000) | 430,000,000 | 40 silver [R] |
| 43 | 1/10 of level 43 (10,800,000,000) | 1,080,000,000 | none (sources) |
| 45 | 1/10 of level 45 (15,600,000,000) | 1,560,000,000 | 45 silver [R] |
| 48 | 1/10 of level 48 (27,000,000,000) | 2,700,000,000 | 48 silver [R] |
| 50 | none | 0 | 50 silver [R] |

The live game paid in the same range: about 5.7 million at 25 and 1.08 billion at 43.

## 5. How it works

### 5.1 Files

All new code is fork-owned, in `scripts/hearthdaoc/epics/`, namespace `DOL.GS.HearthDAoC`, LF line endings. The
approved layout plus `EpicStepQuest.cs`, the shared quest base class, which needs its own file because both the
framework and every line's quest types use it.

| File | Job |
|---|---|
| `EpicChain.cs` | The rules, with no server dependencies (section 5.2). Reusable by later lines. |
| `EpicChainScript.cs` | The in-game part (section 5.4). Reusable by later lines. |
| `EpicStepQuest.cs` | The shared quest base class: journal text from the definition, saved stage and progress, events passed to the rules, actions carried out. Reusable by later lines. |
| `ShadowsEpic.cs` | The Guild of Shadows definitions: 13 steps, their offers and stages, NPC names, texts with their tags, rewards per class, XP shares and coin. |
| `ShadowsEpicQuests.cs` | The 13 quest types (section 5.3). |
| `ShadowsEpicItems.cs` | Every Guild of Shadows reward and quest item template, as builders (section 5.5). |
| `ShadowsEpicEncounters.cs` | The encounters in section 3.4. |
| `EpicCommand.cs` | The GM-only `/epic` command (section 5.6). |

### 5.2 The rules class (`EpicChain`)

It takes plain values and returns plain values, so CI tests it with tables of cases.

- **Definitions** (records): a chain has steps. A step has its level; the steps it needs (it needs any one of them; an
  empty set for 7 and 7 SI); the step it closes (the other version of 7 or 11); its giver kind (Camelot trainers,
  Caer Gothwaite trainer, the class's givers, or a named NPC); its journal title; its offer (lines and keywords); its
  stages; rewards per class (an item id, a list of choices, the armour set, or none); its XP share and its coin.
- **Offers:** given the class, level, finished steps, active steps and the giver (kind and class), which step (if any)
  this giver offers. With strict order at most one step is open at a time per giver.
- **Stages:** a stage lists its events in order (talk to an NPC, a keyword to an NPC, an item given to an NPC, a kill
  of a world target with a count, an encounter's credit). Keywords and hand-ins are accepted only in that order; one
  out of order gets the current prompt again; only the last event moves the stage on. Progress inside a stage is a
  saved sub-index. For each event the rules return the actions (say lines, give or take items, add to a counter, pay
  coin, go to a stage, finish with the reward); they don't carry them out.
- **World targets** match by name ignoring case, plus region.
- **Checks** it also answers: which items a stage needs and whether one is to be replaced (section 2.2); the slot
  rule for rewards and quest items (one free slot; six at 50); whether the weapon swap is pending or used; the XP for
  a step (share × the level's XP, section 4.5, from the `GamePlayer` table); and the coin.

### 5.3 Quest types and saved progress

The 13 types derive from `EpicStepQuest` (a `BaseQuest`) and only name their definition:

`DOL.GS.HearthDAoC.ShadowsEpic07`, `ShadowsEpic07SI`, `ShadowsEpic11`, `ShadowsEpic11SI`, `ShadowsEpic15`,
`ShadowsEpic20`, `ShadowsEpic25`, `ShadowsEpic30`, `ShadowsEpic40`, `ShadowsEpic43`, `ShadowsEpic45`, `ShadowsEpic48`,
`ShadowsEpic50`.

Saved progress is stored under these full names, so they never change; a renamed type would orphan players' rows.

- The quest's `Step` is the stage number; finishing sets it to -2, as the engine does.
- The sub-index inside a stage, counters and flags (kills so far, which of Frund and Agisthil are dead, swap pending
  and swap used) are saved properties of the quest, so a logout or restart keeps them. Finished quests load with
  their saved properties, so the swap marks live on the finished 40 step.
- Each type has the engine's four constructors.
- The engine's assign message ("You have been given the <title> quest.") is kept; its finish message is replaced by
  the live wording "You have finished the <title> quest." [V].

### 5.4 The in-game part (`EpicChainScript`)

- **Switch:** server property `hdc_epics` (default on). Off: nothing is offered, active steps are frozen (their quest
  ignores every event), and the item templates are still written, so the armour fixes stay. The quest types always
  load, so saved rows stay valid.
- **Startup:** after the world loads, it finds every NPC the definitions name (by name and region). If a name matches
  more than one NPC there, the one of the expected class gets the handlers (the `GameMerchant` Ley Manton), and the log
  says so. It registers each step's offer on its givers with `AddQuestToGive` (which gives the "!"), then stamps the
  quest instance the engine made for that NPC with its giver (class and kind), so the "!" and the accept check ask the
  rules about that giver: a Mercenary sees no "!" over an Infiltrator trainer, and a Camelot trainer shows no "!" for 7
  SI. It writes the item templates and logs one line, for example `Guild of Shadows epic: 15 trainers, 15 quest NPCs,
  0 missing`, naming any NPC it can't find. A missing NPC disables only the stages that need it.
- **Handlers:** talk and keyword handling use global `Interact` and `WhisperReceive` handlers matched against the NPCs
  found at startup, so they survive an NPC's death and respawn (an NPC's own handlers are dropped when it dies).
  Hand-ins come through the player's give-item event. The global accept-quest event gives a step to the player.
- **Kills:** a step accepts the engine's kill event only when it is addressed to the quest's own player and the target
  is dead, so one kill counts once and an NPC removed from the world (an encounter going away) counts as nothing.
  Encounters are credited only by their own death handler (section 3.4).
- **Items:** finding, taking and replacing quest items searches every slot the player owns (worn, backpack and
  vault), not the engine's backpack-only search. An item given to the wrong NPC is refused with a line and stays put.
- **Markers:** after a step changes, and when the player levels up, the NPCs in view refresh their quest markers for
  that player.

### 5.5 Items in code

`ShadowsEpicItems` builds every template in section 3.5 and section 4 from plain values and writes them over the
database rows at every start, as upstream's Sluaghbinder armour does, so the code and the database never drift. The
builder's signature is `Build(definition, DbItemTemplate target = null)`: at startup it fills the cached row the
database already has (or a new row), and CI calls it with no target. This includes the 30 existing level-50 rows with
the section 4.4 fixes. A change the owner makes to one of these rows in the database is overwritten at the next start;
the code is the place to change them.

### 5.6 The `/epic` command (GM only)

It works on the GM's target if that is a player, otherwise on the GM.

| Command | Does |
|---|---|
| `/epic status` | Shows the active step, stage and sub-index, the counters, the finished steps and the next step. |
| `/epic start <step> [stage]` | `<step>` is `7`, `7si`, `11`, `11si`, `15` … `50`. Deletes the rows (active or finished) and quest items of that step, of every later step and of the other version of 7 or 11 when that is the step; marks every earlier step finished (the classic 7 and 11 unless an SI version was already finished); puts the character on that step at stage 1 or the given stage, with the flags and counters that stage implies and the quest items it needs. It doesn't change the level; use `/player level`. |
| `/epic goto` | Teleports to the NPC or target the current stage needs (for a kill or encounter, beside the spot). |
| `/epic reset` | Removes every chain row (active and finished) and every chain quest item for the character, and says so when it deletes a finished 50 row. |

To check reward stats, the test guide uses the existing `/item create <template id>` with the ids from section 4.

### 5.7 Upstream files touched

- `scripts/quests/Albion/epic/Shadows50.cs`: deleted. `Defenders_50` keeps Lidmann Halsey and is unchanged.
- `scripts/quests/Albion/epic/Academy50.cs`, `scripts/quests/Hibernia/epic/Essence50.cs`,
  `scripts/quests/Midgard/epic/Mystic50.cs`, `scripts/quests/Midgard/epic/Viking50.cs`: each looks its quest NPC
  (Master Ferowl, Brigit, Danica, Elizabeth) up at different X/Y from where it creates it, so a copy saved by a GM
  shows up as a second NPC at every start. One lookup line each changes to the creation X/Y. The CRLF line endings are
  kept; the files have no BOM and none is added.
- `.github/workflows/server-image.yml`: the C# test filter gains
  `|FullyQualifiedName~UT_EpicChain|FullyQualifiedName~UT_ShadowsEpic`.

No trainer or NPC class is changed.

## 6. World data: `deploy/bin/shadows_epic.py`

Run from `world_fixes.py` at the first start after the update, like `battlegrounds.py`: its own savepoint, the marker
`shadows-epic-v1` in `fork_world_fixes`, each step changing a row only while it still holds the value it expects. If
any step fails, only this fix rolls back; the log shows `Shadows epic: not applied (<reason>)`, and the next start
tries again. A world with the marker is left alone, so changes the owner makes later stay.

| Step | Change | Rows on the clean world |
|---|---|---|
| 1 | Restore the named targets from `offline_classic165_removed_mobs` by Mob_ID: Agisthil, Brodic, Sir Dillus, Lieutenant Grimarth, the arawnite messenger. | 5 |
| 2 | Restore the Pennine Ellyll village: every archived row in Pennine Mountains (region 1) named Ellyll/ellyll villager, guard, sage or champion that isn't already back in `Mob`. | 154 (85 villagers, 50 guards, 12 sages, 7 champions; 14 guards are already back) |
| 3 | Restore Delfina's camp: archived Tylwyth Teg rangers in Pennine Mountains within 3,000 units of 7000,57500. | 22 |
| 4 | Restore the old isolationist camp: archived isolationist rows in Pennine Mountains with X 35,000–40,500 and Y 52,500–58,500. | 39 |
| 5 | Move Frund (the live row, Mob_ID 3ce2271f-…, at Black Mtns. South 8608,50568) to 54120,17650 in the red-dwarf camp (Z and heading from section 3.2), if he is still at the old spot. | 1 |
| 6 | Archive the plain `GameNPC` copy of Ley Manton (the one with the weapon merchant on the same spot) into `fork_removed_mobs`. | 1 |
| 7 | Rename finished `DOL.GS.Quests.Albion.Shadows_50` rows (Step -2) to `DOL.GS.HearthDAoC.ShadowsEpic50`; delete a finished `Shadows_50` row whose character already has a `ShadowsEpic50` row; delete unfinished `Shadows_50` rows. | 0 |
| 8 | Count saved `Mob` rows named Master Ferowl, Brigit, Danica or Elizabeth. | 0 |
| 9 | Give epic vests already in inventories their charges: `Charges` 3 where the item's template is one of the five `*EpicVest` ids and `Charges` is 0. | 0 |

- Restored rows keep their Mob_IDs. A row that `hdc spawns restore` already brought back is left in place and taken
  off `fork_restored_mobs`, so `hdc spawns undo` doesn't remove it.
- Lord Elidyn and his two ellyl heroes stay archived; only the encounter copy exists.
- The log line: `Shadows epic: restored 220 spawns, moved Frund, archived 1 Ley Manton copy, Shadows_50: 0 finished
  kept, 0 duplicates removed, 0 unfinished removed, 0 epic vests recharged` and, if step 8 finds more than one row of
  any of the four, `duplicate epic NPCs: <name> ×<n>`.
- Step 7 and the code agree on the type name through a source test (section 9.2).

## 7. Existing characters and the update

- The world fix runs before anyone can log in, so a character's first login after the update already sees the
  migrated `Shadows_50` row.
- No character is marked or skipped ahead; existing characters start at 7 (owner's decision).
- Armour from the old `Shadows_50` stays in its owners' bags. It comes from the same 30 rows, now fixed, and items read
  most values from their template, so it picks up the fixes; the vests' charges come from world-fix step 9.

## 8. Dialogue: style guide and ledger

- **Ledger:** `docs/fork/epics/shadows-dialogue.md`, written with the code. It lists everything players see that the
  chain adds: journal texts, NPC lines, system messages, quest item names, keywords, and invented amounts (coin), by
  step and stage, each tagged [V] with its source (file and line in the research notes or the source page) or [R].
  Spelling corrections to [V] lines are listed. The rulings in sections 3.2 and 4 are listed there too. In the code,
  each [R] line carries a `// [R]` comment.
- **Style, from the surviving lines:**
  - NPCs speak in plain, slightly formal sentences, without modern words or jokes.
  - Keywords sit inside the sentence in brackets, as in "should you be [interested]".
  - Rhodri is a soldier: direct, a little weary, speaks for Sir Bors, addresses the player by name and by class
    ("Farewell Infiltrator.").
  - Twr ap Alsig is wary of Camelot's people at first and warm once won over; he addresses the player by race ("Yes
    Inconnu?").
  - Trainers open with a greeting and an offer: "Hail! I trust you are well? I have a mission for you should you be
    [interested]."
  - Journal texts are one or two sentences in the imperative, naming the NPC and the place.
  - The live journal's quirks (for example "Travels" capitalised) are kept in [V] lines and not copied into [R] lines.

## 9. Testing

### 9.1 C# unit tests (CI)

Added to the CI filter.

- **`UT_EpicChain`** (rules, tables of cases):
  - offers: each class × each giver (both Camelot trainers and the Caer Gothwaite trainer for 15, 40, 43, 45 and 48;
    Rhodri for 20, 25, 30 and 50) × levels below, at and above each step × finished sets; other classes, other
    trainers and other towns get nothing; the SI/classic lock both ways; either version unlocks the next step;
  - the offer flow: keywords in order, the yes/no after the last one, a decline starting over;
  - each stage of each step against every event it accepts and some it must ignore (wrong NPC, wrong item, keyword
    out of order, a kill while not carrying the Crediac or not wearing the cloak, a kill for a class outside the five);
  - counters (two elementals; Frund and Agisthil in either order); duplicate kill events count once;
  - lost items: replaced only while needed and not held; never a second copy;
  - the slot rule (none free → wait; five free at 50 → wait; six → armour); the weapon swap: pending, used, refused
    after;
  - XP: built from `GamePlayer.GetExperienceAmountForLevel` and checked against the section 4.5 table exactly; coin.
- **`UT_ShadowsEpic`** (data):
  - 13 steps at levels 7, 7, 11, 11, 15, 20, 25, 30, 40, 43, 45, 48, 50; 7 and 7 SI need nothing and close each other;
    11 and 11 SI each need either 7 and close each other; 15 needs either 11; every later step needs the one before;
  - every class has a reward at every item step and none at 48, and 40 has the section 4.3 choices;
  - every item id the definitions name is built by `ShadowsEpicItems`, and every built id is used;
  - the 13 type full names equal the section 5.3 list;
  - spot checks of built items: one per class per kind (jewel, armour, weapon, staff), Spark of Midnight and Crackling
    Impaler open to both classes, the section 4.4 fixes, and the quest item flags.

### 9.2 Python tests (CI, against the clean world)

- Every named NPC and named target the definitions use stands in the expected region near the expected loc, exactly
  once (after the fix where it is restored or moved); generic targets (lesser water elementals, drakoran neophytes,
  renegade guards, cythraul) have at least the 3.2 count in their area.
- The fix: the row counts in section 6; restored rows equal their archive rows; Frund moved to the 3.2 spot; the Ley
  Manton copy archived; the `Shadows_50` migration on synthetic rows (finished kept and renamed, unfinished deleted, a
  character with both rows keeps one); the vest charges on a synthetic inventory row; a second run changes nothing; an
  injected failure in each step rolls back only this fix; the `hdc spawns` takeover.
- Source checks: `Shadows50.cs` stays deleted; the four lookup lines match their creation lines; the type name in
  step 7 matches `ShadowsEpicQuests.cs`; the filter includes the new tests.

### 9.3 Container smoke test

After start: the `Guild of Shadows epic:` line with `0 missing`, the `Shadows epic:` world-fix line, and `/epic`
registered as GM-only (`Command - '&epic' ... required plvl:2`, as the smoke test already checks for `/tele`).

### 9.4 In game (owner, after merge)

The PR adds `docs/fork/verification/sub4-test-guide.md`, written for speed:

- a short list of the GM commands used, each checked against this build before it is listed: `/player level`,
  `/player class`, `/jump`, `/epic`, `/item create`, and damage and healing helpers;
- for each step: the level to set, `/epic start <step>`, each `/epic goto` hop, what to say, hand in or kill, and the
  message or item to expect.

What to run: one full run with one class; the SI branch at 7 and 11 (Adam Glaze taking the worn cloak); the weapon
choice and the swap at 40; the armour with a full bag; turning a step down and getting it offered again; losing a
quest item; logging out mid-step; a Mercenary seeing no "!" over an Infiltrator trainer; talking to Rhodri after he
has died and respawned; a `/spawn` companion's kill counting; and `/item create` for each class's rewards to check
their stats. The owner confirms the old-frontier spots and the assassin's landings there. Results go in
`docs/fork/verification/sub4-ingame.md`, like sub-project 5.

## 10. Documentation and bookkeeping

- `docs/fork/FORK.md`: the chain, the `/epic` command, the `hdc_epics` property, the world fix, and the upstream files
  touched or deleted (section 5.7).
- `docs/fork/CHANGELOG.md`: the release entry.
- `docs/fork/epics/shadows-dialogue.md`: the ledger (section 8).
- `docs/fork/verification/sub4-test-guide.md`: the test guide.
- Tracker #49 (the fork's own): after the owner's in-game run, notes on the `Shadows50` fix and the lookup fix as
  possible upstream candidates. Nothing is sent upstream.

## 11. Risks

1. **Upstream curation.** Restoring 220 archived spawns goes against upstream's 1.65 cleanup, and the 0.35 sync (#50)
   may touch the same tables. The marker and the expected-value checks keep the fix from fighting a later sync, but the
   sync must look at these rows.
2. **Bots.** Bots roam the old frontier zones. Encounter monsters come back for the player who needs them; world
   targets respawn normally; quest NPCs may be pulled into fights near Rhodri or Kuebler (their handlers survive a
   death and respawn).
3. **Permanent names.** The 13 type names can never change.
4. **Written text.** Most dialogue for 7, 11, 45, 48 and 50 is written new; the style guide and the ledger keep it
   consistent and visible.
5. **Locations.** Old-frontier spots and the assassin's landings are confirmed only in the owner's run. Restored
   roaming monsters have untested paths on the old frontier.
6. **Testing cost.** CI can't run the quest engine itself (it needs a running server and database); the in-game run is
   the real test, and the `/epic` command keeps it short.
7. **Item rows.** The code overwrites the Guild of Shadows item rows at every start; database edits to them don't
   last.
8. **Shared NPCs.** Sir Tilian and Tage are also used by Church quests; later lines must share the handlers on them.
9. **Difficulty.** Lord Elidyn (59, with 14 restored ruin guards and level 57–60 heroes) and Belgrik are group fights,
   as on live. `/spawn` companions' kills credit their owner (the engine counts a companion like a pet); the test guide
   confirms it once.
