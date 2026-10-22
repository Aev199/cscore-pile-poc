#!/usr/bin/env python3
"""Apply reviewed native CScore fixes to the exact pinned OpenCS checkout."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

ROOT=Path(__file__).resolve().parents[1]


def apply_patches(source,lock_path):
    source,lock_path=Path(source).resolve(),Path(lock_path).resolve()
    lock=json.loads(lock_path.read_text(encoding='utf8'))
    def git(*args):
        return subprocess.run(['git','-C',str(source),*args],capture_output=True,text=True)
    head=git('rev-parse','HEAD')
    if head.returncode or head.stdout.strip()!=lock['commit']:
        raise ValueError('OpenCS commit differs from the pinned commit')
    statuses=[]
    for entry in lock.get('patches',[]):
        patch=lock_path.parent.parent/entry['file']
        if hashlib.sha256(patch.read_bytes()).hexdigest()!=entry['sha256']:
            raise ValueError('OpenCS patch hash mismatch: '+entry['file'])
        flags=['--ignore-space-change','--whitespace=nowarn']
        if git('apply','--check',*flags,str(patch)).returncode==0:
            result=git('apply',*flags,str(patch))
            if result.returncode:raise ValueError('Cannot apply OpenCS patch: '+result.stderr)
            statuses.append('applied')
        elif git('apply','--reverse','--check',*flags,str(patch)).returncode==0:
            statuses.append('already_applied')
        else:
            raise ValueError('Cannot apply OpenCS patch to changed or partial source: '+entry['file'])
    return statuses


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path,default=ROOT/'vendor/OpenCS')
    args=parser.parse_args()
    for status in apply_patches(args.source,ROOT/'config/opencs.lock.json'):
        print('OpenCS native patch: '+status)
