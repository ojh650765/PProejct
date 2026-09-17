import sys,tempfile,unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from deploy_webgl import version_player_urls,GateFailure

class PlayerCacheVersionTests(unittest.TestCase):
    def test_payload_changes_get_new_urls_and_repeat_is_stable(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);(root/'Build').mkdir()
            rows=[]
            for key in ['loaderUrl','dataUrl','frameworkUrl','codeUrl']:
                (root/'Build'/key).write_bytes(key.encode())
                rows.append(key+' = buildUrl + "/'+key+'";')
            index=root/'index.html';index.write_text('\n'.join(rows))
            version_player_urls(folder);first=index.read_text()
            self.assertEqual(first.count('?v='),4)
            version_player_urls(folder);self.assertEqual(first,index.read_text())
            (root/'Build'/'dataUrl').write_bytes(b'new game data')
            version_player_urls(folder);second=index.read_text()
            self.assertEqual(sum(a!=b for a,b in zip(first.splitlines(),second.splitlines())),1)

    def test_missing_player_urls_refuse_publication(self):
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder)/'index.html').write_text('not a player')
            with self.assertRaises(GateFailure):version_player_urls(folder)
