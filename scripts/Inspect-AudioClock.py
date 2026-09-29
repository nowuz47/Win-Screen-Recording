"""Compare pre-mapping PCM, saved PCM and render clock from an opt-in tone probe.

Diagnostic attribution, not an alternative to Inspect-Audio.py's quality gates.
"""
import argparse, array, bisect, hashlib, json, math, statistics, sys, wave
from pathlib import Path
RATE = 48000

def floats(path):
    data = array.array('f', path.read_bytes())
    if sys.byteorder != 'little': data.byteswap()
    return list(data[::2])

def amplitude(data, hz):
    # Goertzel evaluates the specified frequency directly, without an FFT-bin approximation.
    omega = 2 * math.pi * hz / RATE
    coefficient = 2 * math.cos(omega)
    a = b = 0.0
    for value in data:
        a, b = value + coefficient * a - b, a
    return 2 * math.sqrt(max(0, a*a + b*b - coefficient*a*b)) / len(data)

def signal(data, offset, expected):
    samples = data[offset:offset+4800]
    if len(samples) != 4800 or any(not math.isfinite(x) for x in samples): raise ValueError('Invalid signal window')
    rms = math.sqrt(sum(x*x for x in samples)/len(samples))
    peak = max((amplitude(samples, expected + i*.25), expected + i*.25) for i in range(-80,81))
    return dict(expectedHz=expected, expectedAmplitude=amplitude(samples,expected),
        expectedToneFraction=amplitude(samples,expected)/(math.sqrt(2)*rms) if rms else 0,
        peakHz=peak[1], peakAmplitude=peak[0], rms=rms)

def slope(points):
    if len(points)<2: return None
    origin_x, origin_y = points[0]
    xs = [x-origin_x for x,y in points]; ys = [y-origin_y for x,y in points]
    mx, my = statistics.mean(xs), statistics.mean(ys)
    denominator = sum((x-mx)**2 for x in xs)
    return sum((x-mx)*(y-my) for x,y in zip(xs,ys))/denominator if denominator else None

def inspect(root):
    paths = [root/'audio/system-raw-packets.jsonl',root/'audio/system-raw-48000.f32',root/'audio/system-raw-info.json',
        root/'fixture-render-clock.jsonl',root/'fixture-render-info.json',root/'fixture-tone-stereo-48000.f32']
    packets = [json.loads(line) for line in paths[0].read_text().splitlines()]
    raw = floats(paths[1]); raw_info = json.loads(paths[2].read_text())
    polls = [json.loads(line) for line in paths[3].read_text().splitlines()]
    info = json.loads(paths[4].read_text()); reference = floats(paths[5])
    if raw_info['omittedPackets'] or len(raw)!=raw_info['frames']: raise ValueError('Incomplete raw diagnostic capture')
    mapped=[]
    for path in sorted((root/'audio').glob('system-*.wav')):
        with wave.open(str(path)) as w:
            if (w.getnchannels(),w.getframerate(),w.getsampwidth())!=(2,48000,2): raise ValueError('Unexpected WAV')
            pcm=array.array('h',w.readframes(w.getnframes()))
            if sys.byteorder!='little':pcm.byteswap()
            mapped.extend(v/32768 for v in pcm[::2])
        paths.append(path)
    # Device resets (pause/resume) form separate epochs. Do not bridge them.
    groups=[[]]
    for packet in packets:
        if groups[-1] and packet['devicePosition']!=groups[-1][-1]['devicePosition']+groups[-1][-1]['frames']:groups.append([])
        groups[-1].append(packet)
    clock=[]
    for group in groups:
        if len(group)<2: continue
        clock.append(dict(packets=len(group),firstQpc100ns=group[0]['qpc100ns'],lastQpc100ns=group[-1]['qpc100ns'],
            sampleClockRateRelativeToQpc=slope([(p['qpc100ns']/1e7,p['devicePosition']/RATE) for p in group]),
            packetDurationRatioMinimum=min((b['qpc100ns']-a['qpc100ns'])/1e7*RATE/a['frames'] for a,b in zip(group,group[1:])),
            packetDurationRatioMaximum=max((b['qpc100ns']-a['qpc100ns'])/1e7*RATE/a['frames'] for a,b in zip(group,group[1:]))))
    windows=[]
    for t in (1.5,2,2.5,3,5,6,7,8):
        expected=440 if t<4 else 660
        offset=round(t*RATE)
        windows.append(dict(nominalSeconds=t,raw=signal(raw,offset,expected),mapped=signal(mapped,offset,expected)))
    render_windows=[]; first=polls[0]['qpc100ns']
    for begin,end in [(1,3.5),(6.5,10.5)]:
        selected=[p for p in polls if first+begin*1e7<=p['qpc100ns']<first+end*1e7 and p['clockResult']==0]
        render_windows.append(dict(beginSeconds=begin,endSeconds=end,observations=len(selected),
            deviceClockRateRelativeToQpc=slope([(p['deviceQpc100ns']/1e7,p['devicePosition']/info['clockFrequency']) for p in selected]),
            consumedFramesRateRelativeToQpc=slope([(p['qpc100ns']/1e7,(p['submittedFrames']-p['paddingFrames'])/RATE) for p in selected])))
    return dict(scope='Diagnostic attribution of synthetic fixture capture only; original quality gates are unchanged.',
        files=[dict(path=str(p.relative_to(root)),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths],
        captureClockEpochs=clock,signalWindows=windows,
        independentlyMeasuredMaximumMappedClockResidualMs=max((abs(p['mappedQpc100ns']-p['qpc100ns'])/1e4 for p in packets if 'mappedQpc100ns' in p),default=None),
        render=dict(info=info,zeroPaddingObservations=sum(p['paddingFrames']==0 for p in polls),
            minimumPaddingFrames=min(p['paddingFrames'] for p in polls),
            maximumPollGapMs=max((b['qpc100ns']-a['qpc100ns'])/1e4 for a,b in zip(polls,polls[1:])),
            inaccurateClockReadings=sum(p['clockResult']!=0 for p in polls),windows=render_windows),
        submittedReference=[dict(nominalSeconds=t,**signal(reference,round(t*RATE),440 if t<4 else 660)) for t in (2,7)])

def main():
    parser=argparse.ArgumentParser();parser.add_argument('capture',type=Path);args=parser.parse_args()
    report=inspect(args.capture);out=args.capture/'audio-clock-inspection.json';out.write_text(json.dumps(report,indent=2,allow_nan=False)+'\n')
    print(out)
    print('Render:',json.dumps(report['render']))
    print('Capture clocks:',json.dumps(report['captureClockEpochs']))
    for w in report['signalWindows']:print(w['nominalSeconds'],'raw peak',w['raw']['peakHz'],'mapped peak',w['mapped']['peakHz'],'tone fraction',round(w['mapped']['expectedToneFraction'],3))

if __name__=='__main__': main()
