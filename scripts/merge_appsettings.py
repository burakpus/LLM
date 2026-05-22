#!/usr/bin/env python3
"""
Deep-merge server's appsettings.json (has secrets) with the git version.
Server values win on conflict; keys that exist only in git are added.
"""
import json, os, sys

GIT_PATH = '/tmp/setllm-publish/appsettings.json'
SRV_PATH = os.path.expanduser('~/setllm-api/appsettings.json')

def deep_merge(base, override):
    """base = git defaults, override = server values (take precedence)."""
    result = dict(base)
    for k, v in override.items():
        if k in result and isinstance(result[k], dict) and isinstance(v, dict):
            result[k] = deep_merge(result[k], v)
        else:
            result[k] = v
    return result

git_cfg = json.load(open(GIT_PATH, encoding='utf-8'))
srv_cfg = json.load(open(SRV_PATH, encoding='utf-8'))

merged = deep_merge(git_cfg, srv_cfg)

with open(GIT_PATH, 'w', encoding='utf-8') as f:
    json.dump(merged, f, indent=2, ensure_ascii=False)

print('appsettings.json merged OK')
