# -*- coding: utf-8 -*-
"""
シャワーの水音(継ぎ目のないループ WAV)をノイズから合成する。外部素材は使わない自作の合成音。

出力(既定): <プロジェクト>/Assets/garakshop/shower_kit/Audio/ShowerLoop.wav
  (44.1 kHz / 16 bit / モノラル / 3.0 秒 = 132,300 サンプル、約258 KB)
  プロジェクトのルートはこのスクリプトの位置(<プロジェクト>/Packages/jp.garakshop.shower/Tools~/)の 3 つ上。
  ルートに Packages と Assets が両方無ければエラーで止まる。--out <dir> で <dir>/ShowerLoop.wav に書く。
  パッケージの中(Packages/jp.garakshop.shower/)へは書き込まない(パッケージ更新で上書きされるため)。
  同梱の Runtime/Audio/ShowerLoop.wav はこのスクリプトの出力で、ShowerKit.prefab の ShowerHead の
  AudioSource(Loop)が再生する。作り直すのは保守者だけ(Tools~/README.md)。

合成の方法(すべて「ループ長 N サンプルの周期信号」として作るので、先頭と末尾が数学的に連続する):
  1. ホワイトノイズを FFT し、周波数領域で帯域を掛けて逆 FFT する(円周畳み込み = 周期的なフィルタ)。
       hiss   : 中心 4.8 kHz・幅 ±1.1 oct(対数ガウス) 水が床・浴槽・体に当たるシャーという音の本体
       body   : 中心 0.9 kHz・幅 ±1.0 oct、hiss 比 0.5  水が当たる「ザー」という厚み
       bubble : 中心 0.2 kHz・幅 ±0.8 oct、hiss 比 0.35 排水・浴槽の泡立ちの低域(ゆっくり揺らす)
     いずれも 60 Hz 以下をカットする。
  2. 各帯域に緩い振幅変調(水滴の粒立ち)を掛ける。変調も「周波数を整数ビンだけ持つ低域ノイズ」なので周期的。
       hiss   : 25 Hz 以下の変調、深さ 0.18
       body   : 12 Hz 以下の変調、深さ 0.25
       bubble : 8 Hz 以下の変調、深さ 0.5
  3. 短い水滴の「パチパチ」(2.5〜7 kHz の減衰正弦、2〜7 ms)を 90 個/秒でランダムに足す。
     ループ端を越える分は先頭へ巻き戻して足す(円周加算)ので、周期性は壊れない。
  4. ピークが -6.6 dBFS(0.47)になるよう正規化し、TPDF ディザで 16 bit 化する。
ループ端のクロスフェードは不要(周期合成なので x[N-1] の次の標本が x[0] にそのままつながる)。
生成後に「端の差 |x[0]-x[N-1]|」と「全体の隣接標本差の分布」を標準出力へ出す。

使い方:  py Packages/jp.garakshop.shower/Tools~/make_shower_loop_sound.py [--out <dir>]
         (numpy が必要。乱数は固定シードで再現可能)
音量・厚み・長さを変えたいときはこの冒頭の定数を直して再実行し、Unity で再インポートする。
"""
import argparse
import os
import sys
import wave
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
PKG_DIR = os.path.normpath(os.path.join(HERE, ".."))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
DEFAULT_OUT_DIR = os.path.join(ROOT, "Assets", "garakshop", "shower_kit", "Audio")
OUT = None  # main() で決める

SR = 44100
SECONDS = 3.0
N = int(round(SR * SECONDS))
PEAK = 0.47            # -6.6 dBFS
SEED = 20261022

# (中心 Hz, 幅 oct, 相対ゲイン, 変調の上限 Hz, 変調の深さ)
BANDS = {
    "hiss":   (4800.0, 1.1, 1.00, 25.0, 0.18),
    "body":   (900.0,  1.0, 0.50, 12.0, 0.25),
    "bubble": (200.0,  0.8, 0.35, 8.0,  0.50),
}
HIGHPASS_HZ = 60.0
DROPS_PER_SECOND = 90
DROP_GAIN = 0.55       # hiss に対する水滴パチパチの大きさ(標準偏差比)


def periodic_band_noise(rng, fc, width_oct):
    """周期 N の帯域ノイズ(標準偏差 1)。周波数領域で対数ガウス型の帯域を掛ける。"""
    freqs = np.fft.rfftfreq(N, 1.0 / SR)
    spec = np.fft.rfft(rng.standard_normal(N))
    with np.errstate(divide="ignore"):
        lg = np.log2(np.maximum(freqs, 1e-3) / fc)
    h = np.exp(-0.5 * (lg / width_oct) ** 2)
    hp = np.clip((freqs - HIGHPASS_HZ * 0.5) / (HIGHPASS_HZ * 0.5), 0.0, 1.0)  # 30〜60 Hz で立ち上げ
    h *= hp
    x = np.fft.irfft(spec * h, N)
    return x / x.std()


def periodic_lowpass_noise(rng, cutoff_hz):
    """周期 N のゆっくり変わるノイズ(標準偏差 1、cutoff_hz 以下だけ)。"""
    freqs = np.fft.rfftfreq(N, 1.0 / SR)
    spec = np.fft.rfft(rng.standard_normal(N))
    h = (freqs <= cutoff_hz) & (freqs > 0.4)
    x = np.fft.irfft(spec * h, N)
    return x / x.std()


def add_drops(rng, out, count, gain):
    """短い減衰正弦(水滴のパチパチ)をランダムな位置へ足す。端を越える分は先頭へ巻き戻す。"""
    for _ in range(count):
        f = rng.uniform(2500.0, 7000.0)
        ln = int(rng.uniform(0.002, 0.007) * SR)
        t = np.arange(ln) / SR
        burst = np.sin(2 * np.pi * f * t + rng.uniform(0, 6.28)) * np.exp(-t / (ln / SR / 4.0))
        burst *= gain * rng.uniform(0.3, 1.0)
        pos = int(rng.integers(0, N))
        idx = (pos + np.arange(ln)) % N
        out[idx] += burst


def resolve_out(argv=None):
    ap = argparse.ArgumentParser(prog="make_shower_loop_sound.py")
    ap.add_argument("--out", help="WAV の出力フォルダ(既定: <プロジェクト>/Assets/garakshop/shower_kit/Audio)")
    args = ap.parse_args(argv)
    if args.out:
        out_dir = os.path.abspath(args.out)
    else:
        if not (os.path.isdir(os.path.join(ROOT, "Packages")) and os.path.isdir(os.path.join(ROOT, "Assets"))):
            sys.exit("error: project root not found (expected Packages/ and Assets/ in %s). Use --out <dir>." % ROOT)
        out_dir = DEFAULT_OUT_DIR
    out = os.path.join(out_dir, "ShowerLoop.wav")
    p, pkg = os.path.normcase(os.path.abspath(out)), os.path.normcase(os.path.abspath(PKG_DIR))
    try:
        inside = os.path.commonpath([p, pkg]) == pkg
    except ValueError:  # 別ドライブ
        inside = False
    if inside:
        sys.exit("error: refusing to write inside the package (%s): %s" % (PKG_DIR, out))
    return out


def main():
    global OUT
    OUT = resolve_out()
    rng = np.random.default_rng(SEED)
    mix = np.zeros(N)
    for name, (fc, width, gain, mod_hz, depth) in BANDS.items():
        band = periodic_band_noise(rng, fc, width)
        mod = 1.0 + depth * periodic_lowpass_noise(rng, mod_hz)
        mod = np.maximum(mod, 0.2)
        mix += gain * band * mod
    mix /= mix.std()
    add_drops(rng, mix, int(DROPS_PER_SECOND * SECONDS), DROP_GAIN)

    mix *= PEAK / np.abs(mix).max()
    dither = (rng.random(N) - rng.random(N)) / 32768.0
    pcm = np.round((mix + dither) * 32767.0).clip(-32768, 32767).astype("<i2")

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with wave.open(OUT, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())

    x = pcm.astype(np.float64) / 32768.0
    d = np.abs(np.diff(x))
    seam = abs(x[0] - x[-1])
    rms = np.sqrt(np.mean(x * x))
    print("file     :", OUT, os.path.getsize(OUT), "bytes")
    print("length   : %d samples = %.3f s @ %d Hz, mono 16 bit" % (N, N / SR, SR))
    print("peak     : %.4f = %.2f dBFS   rms %.4f = %.2f dBFS" % (np.abs(x).max(), 20 * np.log10(np.abs(x).max()), rms, 20 * np.log10(rms)))
    print("clipped  :", int(np.sum(np.abs(pcm) >= 32767)))
    print("seam |x[0]-x[N-1]| = %.5f   neighbour diff mean %.5f  p99 %.5f  max %.5f" % (seam, d.mean(), np.percentile(d, 99), d.max()))
    # 継ぎ目をまたいだ前後 1 ms の RMS と全体の RMS の比(振幅が連続しているか)
    k = SR // 1000
    around = np.concatenate([x[-k:], x[:k]])
    print("rms 1ms across seam %.4f vs overall %.4f" % (np.sqrt(np.mean(around ** 2)), rms))


if __name__ == "__main__":
    main()
