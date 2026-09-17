"""Scaffold a scene actor, dialogue, and safely bracketed episode as one change.

Defaults to preview. Pass --write to update the three authoring books, then rebuild Unity.
"""
import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
STORY = ROOT / 'Assets/Game/Data/Story'


def scaffold(args):
    if not re.fullmatch(r'[a-z][a-z0-9_]+', args.id):
        raise ValueError('Episode id must use lowercase letters, numbers, and underscores.')
    if not (ROOT / 'Assets/Game/Scenes' / (args.scene + '.unity')).is_file():
        raise ValueError('Create and register the destination scene before adding its episode.')
    paths = [STORY/'Resources/episodes.json', STORY/'Resources/dialogue.json', STORY/'interior_actors.json']
    episodes, dialogue, actors = [json.loads(p.read_text(encoding='utf-8')) for p in paths]
    if any(e['Id'] == args.id for e in episodes['Episodes']):
        raise ValueError('This episode already exists.')
    if any(e['SequenceId'] == args.id for e in dialogue['Sequences']):
        raise ValueError('This dialogue already exists.')
    actor = args.actor or 'NPC_' + args.id
    if any(a['scene'] == args.scene and a['actor'] == actor for a in actors['actors']):
        raise ValueError('The actor already has an episode binding; extend that episode instead.')
    flag = 'story.' + args.id + '_done'
    beats = [{'Kind': 5}]
    if args.shot:
        shots = json.loads((STORY/'Resources/shots.json').read_text(encoding='utf-8'))
        if not any(s['Name'] == args.shot for s in shots['Shots']):
            raise ValueError('The camera shot must exist in shots.json.')
        beats.append({'Kind': 19, 'Id': args.shot})
    beats += [{'Kind': 4, 'Id': args.id}, {'Kind': 6}]
    episode = {'Id': args.id, 'Scene': args.scene, 'CompletionFlag': flag, 'Beats': beats}
    sequence = {'SequenceId': args.id, 'PlaysOnlyOnce': False,
                'Lines': [{'SpeakerName': args.speaker, 'Text': line} for line in args.line]}
    binding = {'scene': args.scene, 'actor': actor, 'npcId': 'npc_' + args.id,
               'displayName': args.speaker, 'personKey': args.person,
               'episode': args.id, 'requiresFlag': args.requires or '',
               'completionFlag': flag, 'position': args.position, 'radius': 2.4}
    episodes['Episodes'].append(episode)
    dialogue['Sequences'].append(sequence)
    actors['actors'].append(binding)
    if args.write:
        for path, content in zip(paths, (episodes, dialogue, actors)):
            path.write_text(json.dumps(content, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    return {'written': args.write, 'episode': episode, 'dialogue': sequence, 'actor': binding}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('id')
    parser.add_argument('--scene', required=True)
    parser.add_argument('--actor')
    parser.add_argument('--speaker', required=True)
    parser.add_argument('--person', default='professor')
    parser.add_argument('--position', type=float, nargs=3, required=True)
    parser.add_argument('--requires')
    parser.add_argument('--shot')
    parser.add_argument('--line', action='append', required=True)
    parser.add_argument('--write', action='store_true')
    print(json.dumps(scaffold(parser.parse_args()), ensure_ascii=False, indent=2))
