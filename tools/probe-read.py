#!/usr/bin/env python3
"""Reads the scheduling probe's files (tests/Shared/SchedulingProbe.cs) as CI uploads them, standard library only.

    python3 tools/probe-read.py [--threshold MS] [--gap MS] FILE...

Each FILE is a probe file (<assembly>-<start>-<pid>.latency.csv), a project's results (<project>.trx) or the node host test's
times (<assembly>-<start>-<pid>.nodehost.csv). For each probe file it prints:
  - the episodes: rows where the pool's queued item waited (dispatch_ms) or the probe's wake overshot (wake_overshoot_ms) by the
    threshold or more (500 ms unless --threshold says), rows less than 3 s apart joined into one, each with the tests that ran in it
    when a .trx is given (a test's start is its endTime less its duration: the trx's startTime is not reliable);
  - the gaps: two rows further apart than --gap (500 ms unless it says), where the probe's own thread wrote nothing;
  - one summary line.
Files of the first probe (six columns) and of the second (eighteen, after a first line of the machine's facts) are both read; the
columns only the second has are printed only for its files. Every .trx given is applied to every probe file given, so name one
project's probe file with that project's .trx in a run (another host's tests would otherwise be listed in its episodes). It reads, it
never decides: no governor follows without a reading (docs/round2/CARRYOVER.md, fix-ci-stall-measure).
"""
import argparse
import csv
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from datetime import datetime, timedelta, timezone

TRX = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
FIRST_SIX = ['utc', 'wake_overshoot_ms', 'dispatch_ms', 'dispatch_pending', 'pool_threads', 'pool_pending']
JOIN_S = 3.0


def when(text):
    """A timestamp of the probe, the trx or the node host file, as an aware UTC datetime (fractions cut to microseconds)."""
    text = text.strip()
    if text.endswith('Z'):
        text = text[:-1] + '+00:00'
    text = re.sub(r'(\.\d{6})\d+', r'\1', text)
    stamp = datetime.fromisoformat(text)
    if stamp.tzinfo is None:
        stamp = stamp.replace(tzinfo=timezone.utc)
    return stamp.astimezone(timezone.utc)


def number(text):
    try:
        return float(text)
    except ValueError:
        return None


def read_probe(path):
    """(facts, columns, rows): facts from the first line when it is the machine's, each row a dict with 'utc' a datetime and the
    other columns numbers (None when a cell does not read). A torn last line (a host stopped mid-write) is left out and counted."""
    facts, columns, rows, torn = {}, None, [], 0
    with open(path, newline='', encoding='ascii', errors='replace') as handle:
        for line in handle:
            line = line.rstrip('\n')
            if not line:
                continue
            if line.startswith('#'):
                facts.update(pair.split('=', 1) for pair in line.split(':', 1)[-1].split() if '=' in pair)
                continue
            cells = next(csv.reader([line]))
            if columns is None:
                if cells[0] != 'utc':
                    raise SystemExit(f'{path}: the first row is not a probe header ({line[:60]})')
                columns = cells
                if columns[:6] != FIRST_SIX:
                    raise SystemExit(f'{path}: the first six columns are not the probe\'s ({",".join(columns[:6])})')
                continue
            if len(cells) != len(columns):
                torn += 1
                continue
            try:
                row = {'utc': when(cells[0])}
            except ValueError:
                torn += 1
                continue
            for name, cell in zip(columns[1:], cells[1:]):
                row[name] = number(cell)
            rows.append(row)
    return facts, columns or [], rows, torn


def read_trx(path):
    """Every test result: (start, end, name, outcome, seconds), the start being endTime less the duration."""
    tests = []
    for result in ET.parse(path).getroot().iter(TRX + 'UnitTestResult'):
        end, duration = result.get('endTime'), result.get('duration')
        if not end or not duration:
            continue
        hours, minutes, seconds = duration.split(':')
        took = timedelta(hours=int(hours), minutes=int(minutes), seconds=float(seconds))
        finish = when(end)
        tests.append((finish - took, finish, result.get('testName'), result.get('outcome'), took.total_seconds()))
    return tests


def quantile(values, q):
    values = sorted(v for v in values if v is not None)
    if not values:
        return float('nan')
    return values[min(len(values) - 1, int(q * len(values)))]


def biggest(rows, name):
    values = [r.get(name) for r in rows if r.get(name) is not None]
    return max(values) if values else None


def total(rows, name):
    values = [r.get(name) for r in rows if r.get(name) is not None]
    return sum(values) if values else None


def short(name, prefix):
    return name[len(prefix):] if prefix and name.startswith(prefix) else name


def episodes(rows, threshold):
    found, current = [], None
    for row in rows:
        if (row.get('dispatch_ms') or 0) < threshold and (row.get('wake_overshoot_ms') or 0) < threshold:
            continue
        if current and (row['utc'] - current[-1]['utc']).total_seconds() < JOIN_S:
            current.append(row)
        else:
            current = [row]
            found.append(current)
    return found


def second_probe_words(rows):
    """What the second probe's columns say of these rows, empty for a first probe's."""
    words = []
    completed = total(rows, 'pool_completed')
    if completed is not None:
        words.append(f'pool items completed {completed:.0f}')
    paused = total(rows, 'gc_pause_ms')
    if paused is not None:
        words.append(f'GC paused {paused:.1f} ms')
    gen2 = [r['gc_gen2'] for r in rows if r.get('gc_gen2') is not None]
    if gen2:
        words.append(f'gen2 {gen2[0]:.0f}->{gen2[-1]:.0f}')
    working = [r['working_set_mb'] for r in rows if r.get('working_set_mb') is not None]
    if working:
        words.append(f'working set {min(working):.0f}-{max(working):.0f} MB')
    user, kernel = total(rows, 'cpu_user_ms'), total(rows, 'cpu_kernel_ms')
    if user is not None and kernel is not None:
        words.append(f'cpu user {user:.0f} ms kernel {kernel:.0f} ms')
    for name, label in (('rest_overshoot_ms', 'rest overshoot'), ('last_write_ms', 'write'), ('pool_read_ms', 'pool read'),
                        ('process_read_ms', 'process read'), ('beat_gap_ms', 'beat gap'), ('beat_age_ms', 'beat age')):
        value = biggest(rows, name)
        if value is not None:
            words.append(f'max {label} {value:.1f} ms')
    return ', '.join(words)


def print_probe(path, tests, threshold, gap):
    facts, columns, rows, torn = read_probe(path)
    name = path.replace('\\', '/').rsplit('/', 1)[-1]
    print(f'== {name}')
    if facts:
        print('   facts: ' + ' '.join(f'{k}={v}' for k, v in facts.items()))
    if not rows:
        print(f'{name}: no rows')
        return
    prefix = ''
    if tests:
        common = Counter(t[2].split('.')[0] for t in tests).most_common(1)
        assembly = name.split('-', 1)[0]
        prefix = assembly + '.' if assembly.startswith(common[0][0]) else ''
    found = episodes(rows, threshold)
    print(f'episodes (dispatch or wake overshoot >= {threshold:.0f} ms, rows under {JOIN_S:.0f} s apart joined): {len(found)}')
    seconds = 0.0
    for episode in found:
        first, last = episode[0]['utc'], episode[-1]['utc']
        seconds += (last - first).total_seconds()
        print(f"\n{first:%H:%M:%S}-{last:%H:%M:%S} ({(last - first).total_seconds():.0f} s) max dispatch "
              f"{biggest(episode, 'dispatch_ms'):.0f} ms, max wake overshoot {biggest(episode, 'wake_overshoot_ms'):.0f} ms, "
              f"pool threads up to {biggest(episode, 'pool_threads'):.0f}, queue up to {biggest(episode, 'pool_pending'):.0f}")
        extra = second_probe_words(episode)
        if extra:
            print('   ' + extra)
        if tests:
            running = [t for t in tests if t[0] <= last + timedelta(seconds=1) and t[1] >= first - timedelta(seconds=1)]
            classes = Counter(short(t[2], prefix).rsplit('.', 1)[0] for t in running)
            print('   classes running: ' + ', '.join(f'{k} x{v}' for k, v in classes.most_common(14)))
            for test in sorted(running, key=lambda t: -t[4])[:5]:
                print(f'     long: {short(test[2], prefix)} {test[3]} {test[4]:.1f} s '
                      f'({test[0]:%H:%M:%S.%f}-{test[1]:%H:%M:%S.%f})')
    holes = []
    for before, after in zip(rows, rows[1:]):
        apart = (after['utc'] - before['utc']).total_seconds() * 1000
        if apart > gap:
            holes.append((before, after, apart))
    print(f'\ngaps (rows more than {gap:.0f} ms apart: the probe\'s own thread wrote nothing): {len(holes)}')
    for before, after, apart in holes:
        words = f"  {before['utc']:%H:%M:%S.%f} for {apart / 1000:.2f} s; the row after: wake overshoot " \
                f"{after.get('wake_overshoot_ms') or 0:.0f} ms, dispatch {after.get('dispatch_ms') or 0:.0f} ms"
        for column, label in (('rest_overshoot_ms', 'rest overshoot'), ('last_write_ms', 'the write before it'),
                              ('pool_read_ms', 'pool read'), ('process_read_ms', 'process read'), ('gc_pause_ms', 'GC pause'),
                              ('beat_gap_ms', 'longest beat gap'),
                              ('beat_age_ms', 'beat age')):
            if after.get(column) is not None:
                words += f', {label} {after[column]:.0f} ms'
        print(words)
    span = (rows[-1]['utc'] - rows[0]['utc']).total_seconds()
    summary = (f"{name}: rows {len(rows)} over {span:.0f} s"
               f"{f' (+{torn} torn)' if torn else ''}; wake overshoot p50 {quantile([r['wake_overshoot_ms'] for r in rows], 0.5):.2f} "
               f"p99 {quantile([r['wake_overshoot_ms'] for r in rows], 0.99):.2f} max {biggest(rows, 'wake_overshoot_ms'):.1f} ms; "
               f"dispatch p50 {quantile([r['dispatch_ms'] for r in rows], 0.5):.2f} p99 {quantile([r['dispatch_ms'] for r in rows], 0.99):.1f} "
               f"max {biggest(rows, 'dispatch_ms'):.1f} ms; pool threads max {biggest(rows, 'pool_threads'):.0f}, queue max "
               f"{biggest(rows, 'pool_pending'):.0f}; episodes {len(found)} ({seconds:.0f} s); gaps {len(holes)}")
    extra = second_probe_words(rows)
    print('\n' + summary + (f'; {extra}' if extra else '') + f"; {'second' if len(columns) > 6 else 'first'} probe's columns")


def print_node_host(path):
    with open(path, newline='', encoding='ascii', errors='replace') as handle:
        for row in csv.DictReader(handle):
            def at(column):
                return when(row[column]) if row.get(column) else None
            started, process, first, up, written, seen = (at(c) for c in (
                'test_started_child_utc', 'child_process_start_utc', 'child_log_first_utc', 'child_up_utc', 'child_log_written_utc',
                'test_saw_up_utc'))

            def after(a, b):
                return f'{(a - b).total_seconds():+.3f} s' if a and b else 'not known'
            print(f"{path.replace(chr(92), '/').rsplit('/', 1)[-1]}: process started {after(process, started)} after the test "
                  f"started it; its log began {after(first, process)} after that; 'is up' stamped {after(up, process)} after the "
                  f"process started; the log last written {after(written, up)} after the line's stamp; the test saw it "
                  f"{after(seen, up)} after the stamp; {row.get('polls')} reads, at most {row.get('poll_max_gap_ms')} ms apart, the "
                  f"longest {row.get('tail_max_ms')} ms")


def main():
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('files', nargs='+')
    parser.add_argument('--threshold', type=float, default=500.0)
    parser.add_argument('--gap', type=float, default=500.0)
    args = parser.parse_args()
    tests = []
    for path in args.files:
        if path.endswith('.trx'):
            tests.extend(read_trx(path))
    for path in args.files:
        if path.endswith('.trx'):
            continue
        if path.endswith('.nodehost.csv'):
            print_node_host(path)
        else:
            print_probe(path, tests, args.threshold, args.gap)
        print()
    return 0


if __name__ == '__main__':
    sys.exit(main())
