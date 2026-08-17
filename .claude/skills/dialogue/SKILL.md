---
name: dialogue
description: Write or edit Korean dialogue for this game. Use whenever adding, rewriting or reviewing lines in dialogue.json, naming a character, or writing any player-facing Korean text.
---

# Writing dialogue for this game

The game is Korean-first and modelled on the Korean script of Pokémon Diamond/Pearl. Its
dialogue has been rejected five separate times for reading as 번역체 — correct Korean that
nobody speaks. Everything below exists because of a specific line that failed.

## The one rule that fixes most of it

**Write the Korean first. The English is a gloss of the Korean, never the source.**

`dialogue.json` stores `Text` (English) beside `TextKo` (Korean), and that layout invites
writing the English sentence and then rendering it. Rendering produces English clause order
wearing Korean words. Every rejected line came out of that direction.

Compare, from this project:

| Written English-first | Written Korean-first |
|---|---|
| 건너편에 못 건널 뭔가가 있거나, 여기 두고 못 갈 뭔가가 있거나. | 무서운 게 있는 겐지, 두고 못 가는 게 있는 겐지… |
| 이미 정한 포켓몬하고 다퉈 봐야 오후만 버리네. | 한번 정한 녀석은 안 바뀌어. |
| 시작하기 전에 하나 묻겠네. | 그럼 이제 자네에 대한 것을 가르쳐주게나. |

The left column is grammatical and nobody talks like that. "a waste of an afternoon" is an
English idiom; "오후만 버리네" is that idiom with Korean words on it.

## How the source script actually sounds

Read the real thing before writing. The Korean DP script's people **trail off, stammer,
repeat themselves and interrupt themselves**:

- 쥰 (rival): `으왓! 포, 포켓몬!? 뭐야, 대체-!?` / `지각하면 벌금 100만엔이다!?` /
  `후와-! 네 몬스터 굉장했어!` / `…그게 아니라 나도 너도 딴 사람 포켓몬을 써버렸는데 괜찮으려나…?`
- 박사: `흠… 그래, 그렇게 된 거로군.` / `포켓몬을 썼다고? 보여줘 보게나.` /
  `괜찮으면 내가 요모조모로 알려주겠네.`
- 지문: `가방…이네. 좀 전에 그 사람이 놔두고 간 건가 보네. 어쩌지?`

Note what is there: `…`, `-!?`, `아 참`, `어…`, sentence fragments, a question the speaker
answers themselves. Note what is **not** there: subordinate clauses, balanced pairs, tidy
parallel structure. Polished prose is the tell.

## The cast's registers

| Who | Register | Sounds like |
|---|---|---|
| **린든** (professor, 71) | 하게체, unhurried, does not stop mid-thought because someone arrived | `흠… 또 멈췄군.` `미안하네만 지금 적어 놔야 해서.` `가지게. 한번 정한 녀석은 안 바뀌어.` |
| **케스** (rival, teenager) | 반말, always in a hurry, explains nothing, exclaims | `야, 호수 갈 거니까 따라와.` `늦으면 벌금 100만원이다!?` `어… 어느 걸로 할 거야?` |
| **브람** (gate) | 해체/하게체, apologetic, passing on a message rather than enforcing a rule | `어? 벌써 나가려고?` `얘기부터 듣고 오게. 길이야 안 없어지니까.` |

Linden explains too much if you let him. Kes explains nothing — that is what makes following
him the player's own idea rather than an instruction.

## Narration has no speaker

Stage directions use an **empty** `SpeakerId` / `SpeakerName` / `PortraitKey`, like
`op_flock`. The box hides the name plate when there is no name.

Giving narration to a character makes them describe themselves in the third person — the
professor announcing "he has left his bag", or Kes' name plate on the sentence "…가버렸네"
about Kes leaving. Both shipped; both were reported as "누구 말인지 모르겠음".

Narration also thinks out loud rather than describing: `가방…이네. 어쩌지?`, not
`둑에 가방이 놓여 있다.`

## Mechanical rules

- **Numbers are digits**, not spelled out: `100만원`, `71`. Korean speech uses digits.
- **Particles must agree.** Any name spliced into a sentence goes through `PokeLab.Core.Josa`
  — `Josa.WithTopic`, `WithSubject`, `WithObject`. The player types their own name, so the
  particle cannot be authored into the string. `피카츄이 쓰러졌다` is what happens without it.
- **Never invent a number that refers to game state.** A line quoting "기록이 마흔 하나" was
  rejected because 41 corresponds to nothing. Either read the real value or describe the thing
  without counting it.
- `{PLAYER}` is substituted by `EpisodeRunner.Substitute` over every line and choice label.

## Where things are

- Lines: `Assets/Game/Data/Story/Resources/dialogue.json` (`Sequences[].Lines[]`)
- Episode beats that play them: `Assets/Game/Data/Story/Resources/episodes.json`
- Speakers, roles and sprite keys: `Assets/Game/Data/Story/cast.json`
- Portraits resolve `npc_gate_01` → `gate` → `townsman` through `PersonSpriteLibrary`'s alias
  table, then `Resources/Portraits/<key>.png`. The dialogue box and the world sprite must use
  the same table or they draw two different people.

## Before you call a line finished

Read it aloud. If it sounds like a subtitle, rewrite it. If it is balanced, tidy and complete,
it is probably translated. The published script is the standard, and that script is messier
than anything you would write on purpose.
