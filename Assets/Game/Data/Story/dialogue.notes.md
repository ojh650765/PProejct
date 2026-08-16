# dialogue.json — how this book is written

Moved out of the file itself. Unity's `JsonUtility` maps every JSON object onto a C#
class, so an object's keys have to be legal field names — and this block had keys `"0"`
through `"5"` (the tone table) and `"{PLAYER}"` (the substitution note). The whole file
failed to parse because of them, which meant every line of dialogue in the game was
unreachable and the only symptom was one warning per run.

Same lesson as the material files: reasoning about data does not go inside the data.

- **version** — 2
- **authoredBy** — narrative-design
- **shapedFor** — Assets/Game/Scripts/Overworld/Npc/DialogueBook.cs -- DialogueBookEntry / DialogueBookLine / DialogueBookChoice / DialogueBookBinding
- **npcBindings** — Which sequence each townsperson speaks, and which flag switches them to the next one. It lives here rather than on the NPC because the level is generated from slice_layout.json: a sequence id dragged onto an NPC in the scene is thrown away by the next rebuild, and every one of these characters had exactly nothing to say as a result. NpcController reads this whenever its own fields are empty.
- **note** — Every Lines[] entry is a DialogueBookLine: a DialogueLine plus its Korean half. DialogueBook.Build collapses the pair to the single-language DialogueLine the runner and the UI already render, so the language choice is made once, at the seam, and nothing downstream carries two texts around. 'Tone' is an integer because JsonUtility deserialises enums from numbers; 'ToneName' sits beside it for humans and is ignored. Choices jump by line index and -1 ends the sequence, exactly as the struct documents.
- **tones**
  - **0** — Neutral
  - **1** — Friendly
  - **2** — Excited
  - **3** — Worried
  - **4** — Hostile
  - **5** — System
- **conventions**
  - **PortraitKey** — The SpeakerId, on every line that has a speaker. Narration keeps an empty key because it is the world talking, not a person. THE ARTWORK DOES NOT EXIST: Tools/Sprites/people.py produces 32px overworld sheets and nothing else, so DialoguePortraits finds nothing and DialogueView collapses the frame -- which is exactly what it does today. The key is filled in anyway so that dropping five illustrations into Assets/Game/Art/Sprites/Resources/Portraits/ is the whole of the remaining work; see DialoguePortraits for the file names.
  - **{PLAYER}** — Substituted with PlayerProfile.TrainerName, in both languages -- DialogueBook.Build runs the substitution after it has picked a language, so the token works wherever it is written.
  - **AutoAdvanceSeconds** — Non-zero only where the scene should play itself -- the prologue over black and the plaza bulletin. Everything a character says to the player's face waits for a press.
  - **lineLength** — Kept under ~150 characters. DialogueView's body band is two lines of 30pt across a 1480px measure; longer than that scrolls out of the scrim. Korean runs shorter than the English for the same content, which is the normal ratio and not a sign anything was dropped.
  - **koreanRegister** — Taken from the shipped Korean Diamond/Pearl script, not chosen by ear. LINDEN speaks 하게체 and calls the player 자네 -- 나나카마도's endings are ~네 / ~게나 / ~인가 / ~군 / ~겠네, an elder addressing a younger adult with respect. That respect IS the characterisation: the professor is asking a favour, not instructing a small child, and 해라체 with 너 destroys it. He also names himself in his opening lines, as every professor in the series does. KES is blunt 반말 to the player and 해요체 up to the professor, matching 쥰. THE TERMINAL is 합쇼체, matching DP's television report. BRAM, SELA and ODELL are 해요체 -- townsfolk in DP address the player politely, and a stallholder meeting a child in 반말 is too familiar. Bram's firmness comes from short sentences, not from dropping the level.
  - **korean** — Written as a Korean game writes. The English is the source of MEANING, not of sentence shape -- read what a line has to accomplish, then write what a Korean game would put there. If the Korean can be mapped clause-for-clause back onto the English it is wrong even when every word is right. Concrete tells, all of which appeared in the first draft and were rewritten out: '의'-stacked noun phrases (봄철 호수의 수심), English transitive frames that make an abstract noun the actor (기록은 ~을 이어왔다), passive nominalisation (그려진 건 ~이다), and two clauses welded with '-고' because the English used 'and'. Korean stops the sentence instead.
  - **koreanRuns** — Translate in runs, never line by line. Before writing a line, read the two either side and ask what it must carry forward and what it must set up. A line can be perfect Korean in isolation and still broken because its antecedent is in the previous line. Two rules follow. FIRST: reuse the noun the previous line established -- a synonym is not free in Korean, it reads as a new thing, so if line 1 says 기록 then line 2 says 그 기록 and not 장부. SECOND: honour deliberate withholding. op_flock line 0 must not name the birds, because Bram naming 구구 two lines later is the payoff; op_prologue line 1 says 늘 같은 것 so that 포켓몬이다 is still a reveal.
  - **koreanFigures** — When the English uses a rhetorical shape -- a bare noun list, parallel repetition, a dash for an aside -- do not reproduce the shape. Work out what it does and find the Korean device that does it. 'Rainfall. Harvests. The depth of the lake in spring.' as bare Korean nouns forces '의'-stacking on the third; as clauses (비가 언제 왔는지 / 무엇을 얼마나 거뒀는지 / 봄에 호수가 얼마나 깊었는지) it keeps the three-beat accumulation and sounds like someone talking.
  - **koreanFunction** — Translate the function, not the words. op_prologue line 5 exists to ask the player's gender -- its choices set profile.body.male / profile.body.female -- so it is 너는 남자아이니, 여자아이니? as the series asks it, not a rendering of 'How do you see yourself?', which would leave the two buttons under it reading as a non-sequitur.
  - **koreanNumbers** — Figures, not words. English spells quantities out and Korean does not, so 'four hundred years' is 400년 and never 사백 년 -- a spelled-out number is one of the most reliable signs a line came through a translator. Applies to ages, years, distances, item counts, percentages and anything above about ten. The exception is a small count with its counter, which is how Korean actually says it: 세 마리, 한 번, 두 번째 -- writing 3마리 there is worse than the problem it fixes.
  - **koreanPunctuation** — No em-dashes and no semicolons. They are English joins; Korean splits the sentence or uses a comma, and an interrupted or trailing line takes … instead. Emphasis lives in the verb ending and the particle, never in typography.
