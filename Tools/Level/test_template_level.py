import copy
import json
import unittest
from pathlib import Path
from template_level import generate


class TemplateTests(unittest.TestCase):
    def setUp(self):
        self.config=json.loads((Path(__file__).parent/'templates/meadow.json').read_text(encoding='utf-8'))

    def test_repeatable_ground_and_placements(self):
        a=generate(self.config)
        self.assertEqual(a,generate(self.config))
        ground=a['ground'][0]
        self.assertTrue(all(i<len(ground['vertices'])//3 for i in ground['triangles']))
        self.assertEqual(len(ground['normals']),len(ground['vertices']))
        self.assertEqual(len(a['checkpoints']),3)

    def test_rejects_obstructed_road(self):
        self.config['props'][0]['at']=[0,10]
        with self.assertRaises(ValueError):generate(self.config)

    def test_rejects_overlap(self):
        self.config['items'][0]['at']=self.config['props'][0]['at'][:]
        with self.assertRaises(ValueError):generate(self.config)

    def test_rejects_bad_scene_and_relief(self):
        for change in [{'scene':'Town'},{'scene':'Battle'},{'scene':'Interior_Lab'},{'scene':'../outside'},{'relief':5}]:
            config=copy.deepcopy(self.config);config.update(change)
            with self.assertRaises(ValueError):generate(config)


if __name__=='__main__':unittest.main()
