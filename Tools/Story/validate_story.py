"""Validate references across the episode, dialogue, shot and actor authoring books."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
STORY = ROOT/'Assets/Game/Data/Story'

def validate():
    read = lambda path: json.loads(path.read_text(encoding='utf-8'))
    episodes = read(STORY/'Resources/episodes.json')['Episodes']
    sequences = read(STORY/'Resources/dialogue.json')['Sequences']
    shots = read(STORY/'Resources/shots.json')
    actors = read(STORY/'interior_actors.json')['actors']
    failures = []
    def index(rows, key, label):
        result = {}
        for row in rows:
            value = row[key]
            if value in result: failures.append(f'Duplicate {label}: {value}')
            result[value] = row
        return result
    episode_ids = index(episodes, 'Id', 'episode')
    dialogue_ids = index(sequences, 'SequenceId', 'dialogue')
    shot_ids = index(shots['Shots'], 'Name', 'shot')
    timeline_ids = index(shots['Timelines'], 'Name', 'timeline')
    for sequence in sequences:
        lines = sequence.get('Lines', [])
        for index, line in enumerate(lines):
            for choice in line.get('Choices', []):
                destination = choice.get('GoToLine', -1)
                if destination != -1 and not 0 <= destination < len(lines):
                    failures.append(f"{sequence['SequenceId']}: line {index} choice targets missing line {destination}")
    for episode in episodes:
        context = episode['Id']
        scene = episode.get('Scene')
        if scene and not (ROOT/'Assets/Game/Scenes'/f'{scene}.unity').is_file():
            failures.append(f'{context}: missing episode scene {scene}')
        next_id = episode.get('NextEpisodeId')
        if next_id and next_id not in episode_ids: failures.append(f'{context}: missing next episode {next_id}')
        for beat in episode['Beats']:
            if not isinstance(beat.get('Kind'), int) or beat['Kind'] not in range(23):
                failures.append(f'{context}: unsupported beat kind {beat.get("Kind")}')
            book = {4: dialogue_ids, 19: shot_ids, 20: timeline_ids}.get(beat['Kind'])
            if book is not None and beat.get('Id') not in book:
                failures.append(f'{context}: unresolved beat {beat}')
        seen = set()
        current = context
        while current and current in episode_ids:
            if current in seen:
                failures.append(f'{context}: episode chain contains a cycle'); break
            seen.add(current)
            current = episode_ids[current].get('NextEpisodeId')
    for actor in actors:
        if actor['episode'] not in episode_ids:
            failures.append(f"{actor['actor']}: missing episode")
        elif actor['completionFlag'] != episode_ids[actor['episode']]['CompletionFlag']:
            failures.append(f"{actor['actor']}: completion flag does not match its episode")
        scene = episode_ids.get(actor['episode'], {}).get('Scene')
        if scene and scene != actor['scene']:
            failures.append(f"{actor['actor']}: actor scene does not match episode scene")
        if not (ROOT/'Assets/Game/Scenes'/f"{actor['scene']}.unity").is_file():
            failures.append(f"{actor['actor']}: missing scene")
    return {'episodes': len(episodes), 'shots': len(shot_ids), 'timelines': len(timeline_ids),
            'actorBindings': len(actors), 'failures': failures}

if __name__ == '__main__':
    report = validate()
    print(json.dumps(report, indent=2))
    raise SystemExit(bool(report['failures']))
