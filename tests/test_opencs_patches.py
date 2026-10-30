import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

from tools.apply_opencs_patches import apply_patches


class PinnedPatchTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root=Path(self.tmp.name)
        self.source=self.root/'upstream'; self.source.mkdir()
        def git(*args):
            return subprocess.check_output(['git','-C',str(self.source),*args],stderr=subprocess.DEVNULL)
        self.git=git
        git('init'); git('config','user.email','test@example.invalid');git('config','user.name','Test')
        (self.source/'law.txt').write_text('original\n')
        git('add','.');git('commit','-m','baseline')
        pin=git('rev-parse','HEAD').decode().strip()
        (self.source/'law.txt').write_text('corrected\n')
        patch=git('diff');git('checkout','--','law.txt')
        (self.root/'patches').mkdir();(self.root/'config').mkdir()
        self.patch=self.root/'patches/fix.patch';self.patch.write_bytes(patch)
        self.lock=self.root/'config/opencs.lock.json'
        self.lock.write_text(json.dumps(dict(commit=pin,patches=[dict(file='patches/fix.patch',sha256=hashlib.sha256(patch).hexdigest())])))

    def test_applies_once_and_accepts_repeat_without_modifying_again(self):
        self.assertEqual(apply_patches(self.source,self.lock),['applied'])
        self.assertEqual((self.source/'law.txt').read_text(),'corrected\n')
        self.assertEqual(apply_patches(self.source,self.lock),['already_applied'])

    def test_rejects_other_pin_before_writing(self):
        d=json.loads(self.lock.read_text());d['commit']='0'*40;self.lock.write_text(json.dumps(d))
        with self.assertRaisesRegex(ValueError,'commit'):apply_patches(self.source,self.lock)
        self.assertEqual((self.source/'law.txt').read_text(),'original\n')

    def test_rejects_modified_patch_before_writing(self):
        self.patch.write_bytes(self.patch.read_bytes()+b'corrupted\n')
        with self.assertRaisesRegex(ValueError,'hash'):apply_patches(self.source,self.lock)
        self.assertEqual((self.source/'law.txt').read_text(),'original\n')

    def test_rejects_conflicting_source_without_overwriting(self):
        (self.source/'law.txt').write_text('unrelated edit\n')
        with self.assertRaisesRegex(ValueError,'apply'):apply_patches(self.source,self.lock)
        self.assertEqual((self.source/'law.txt').read_text(),'unrelated edit\n')
