"""Integration checks for --tiles-only. Build MFSPLCli in Release first."""
from __future__ import print_function

import argparse
import json
import os
import shutil
import subprocess
import tempfile


def write_json(path, value):
    with open(path, 'w') as handle:
        json.dump(value, handle)


def make_ir(path):
    os.makedirs(path)
    write_json(os.path.join(path, 'tiles.json'), {
        'app': 'Tiles regression',
        'frames': [{'handle': 1, 'name': 'TEST', 'width': 32, 'height': 32, 'tiles': []}]})
    write_json(os.path.join(path, 'images.json'), {'images': {}})


def run_cli(dotnet, cli, source, output):
    process = subprocess.Popen([
        dotnet, cli, '-o', source, '--tiles-godot', output,
        '--tiles-godot-res', 'res://tests/tiles', '--tiles-only'],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    log = process.communicate()[0].decode('utf-8', 'replace')
    return process.returncode, log


def check_export(output, name):
    level = os.path.join(output, name)
    with open(os.path.join(level, 'tilemap.json')) as handle:
        manifest = json.load(handle)
    assert len(manifest['frames']) == 1, manifest
    frame = os.path.join(level, 'frames', 'TEST')
    assert os.path.isfile(os.path.join(frame, 'tileset.tres'))
    with open(os.path.join(frame, 'tilemap.tscn')) as handle:
        scene = handle.read()
    assert 'res://tests/tiles/%s/frames/TEST/tileset.tres' % name in scene, scene


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cli', required=True, help='Path to the built mfspL-cli.dll')
    parser.add_argument('--dotnet', default='dotnet', help='.NET host executable')
    args = parser.parse_args()
    cli = os.path.abspath(args.cli)
    assert os.path.isfile(cli), cli
    root = tempfile.mkdtemp(prefix='nebula-tiles-only-')
    try:
        ir = os.path.join(root, 'ir')
        name = "Test MFA"
        first = os.path.join(ir, name)
        make_ir(first)
        make_ir(os.path.join(ir, 'Other MFA'))
        incomplete = os.path.join(ir, 'Incomplete')
        os.makedirs(incomplete)
        write_json(os.path.join(incomplete, 'tiles.json'), {'frames': []})

        output = os.path.join(root, 'parent-output')
        code, log = run_cli(args.dotnet, cli, ir, output)
        assert code == 0 and '2 levels OK (0 failed)' in log, log
        check_export(output, name)
        check_export(output, 'Other MFA')
        assert sorted(os.listdir(output)) == sorted([name, 'Other MFA'])
        print('PASS parent IR root exports both MFAs and skips incomplete directories')

        # A direct MFA selection must not export unrelated nested directories.
        make_ir(os.path.join(first, 'Nested MFA'))
        for label, source in [('direct', first), ('trailing-separator', first + os.sep)]:
            output = os.path.join(root, label + '-output')
            code, log = run_cli(args.dotnet, cli, source, output)
            assert code == 0 and '1 levels OK (0 failed)' in log, log
            check_export(output, name)
            assert os.listdir(output) == [name]
            print('PASS %s MFA path exports exactly the selected MFA' % label)

        empty = os.path.join(root, 'empty')
        os.makedirs(empty)
        for label, source in [('empty', empty), ('missing-images', incomplete)]:
            output = os.path.join(root, label + '-output')
            code, log = run_cli(args.dotnet, cli, source, output)
            assert code == 3 and 'tile IR dirs not found' in log, log
            assert not os.path.exists(output)
            print('PASS %s input reports failure without creating output' % label)
    finally:
        # Only remove the temporary tree created by this test.
        resolved = os.path.realpath(root)
        assert os.path.dirname(resolved) == os.path.realpath(tempfile.gettempdir())
        assert os.path.basename(resolved).startswith('nebula-tiles-only-')
        shutil.rmtree(resolved)


if __name__ == '__main__':
    main()
