# Tools~(生成スクリプト)

このフォルダは名前が `~` で終わるので、Unity は読み込まない(`.meta` も不要)。パッケージに同梱している素材を作ったスクリプトを、作り直しや改造のために置いている。

| スクリプト | 作るもの | 必要なもの |
|---|---|---|
| `build_shower_kit.py` | `ShowerKit.fbx`(ノード `ShowerHead` / `ShowerMixer` / `ShowerHolder`) | Blender(5.2 LTS で確認) |
| `make_shower_loop_sound.py` | `ShowerLoop.wav`(3.0 秒のループ、44.1 kHz / 16 bit / モノラル) | Python 3 + numpy |

どちらも外部素材を使わない(形は手続き生成、音はノイズからの合成)。音は固定シードなので、同じ環境なら毎回同じ WAV になる。

## 出力先

既定では、パッケージを入れた**プロジェクトの `Assets/garakshop/shower_kit/`** に書く。

- メッシュ: `Assets/garakshop/shower_kit/Meshes/ShowerKit.fbx`
- 音: `Assets/garakshop/shower_kit/Audio/ShowerLoop.wav`

プロジェクトのルートは、スクリプトの場所(`<プロジェクト>/Packages/jp.garakshop.shower/Tools~/`)の 3 つ上として求める。そこに `Packages` と `Assets` が両方無いとエラーで止まる(VPM のキャッシュなど、プロジェクトの外から実行したとき)。その場合は出力先を指定する。

**パッケージの中(`Packages/jp.garakshop.shower/`)には書き込まない**(パッケージを更新すると上書きされるため)。出力先にパッケージの中を指定してもエラーで止まる。

## 実行方法

プロジェクトのルートで(Windows の例):

```
"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe" -b --factory-startup --python Packages/jp.garakshop.shower/Tools~/build_shower_kit.py
"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe" -b --factory-startup --python Packages/jp.garakshop.shower/Tools~/build_shower_kit.py -- --out D:/work/shower --save-blend D:/work/shower/ShowerKit.blend

py Packages/jp.garakshop.shower/Tools~/make_shower_loop_sound.py
py Packages/jp.garakshop.shower/Tools~/make_shower_loop_sound.py --out D:/work/shower
```

- `build_shower_kit.py` の引数は Blender の `--` の後ろに書く。`--out <dir>` で `<dir>/ShowerKit.fbx` に書く。`--save-blend <path>` を付けたときだけ `.blend` も保存する(既定では保存しない)。
- `make_shower_loop_sound.py --out <dir>` で `<dir>/ShowerLoop.wav` に書く。
- 生成後に寸法・三角形数(メッシュ)、長さ・ピーク・継ぎ目の差(音)を標準出力へ出す。

作った FBX / WAV を自分のプレハブで使うときは、取り込み設定を同梱品に合わせる(FBX: `globalScale` 1、`bakeAxisConversion` オフ、`materialImportMode` None。マテリアルは同梱の `Runtime/Materials/ShowerChrome.mat`・`ShowerWhiteResin.mat` を割り当てる)。同梱の `ShowerKit.prefab` は同梱の素材を参照しているので、生成しただけでは変わらない。

## 保守者の手順(同梱の素材を作り直すとき)

1. 上の既定(またはパッケージの外の一時フォルダ)へ出力する。
2. 出力を同梱品と比べる。メッシュはノード名・頂点数・三角形数・bounds、音は長さ・ピーク(形や合成の定数を変えていなければ一致する。FBX はファイル内の作成日時だけ違う)。
3. 出力を Unity の外で `Packages/jp.garakshop.shower/Runtime/Meshes/ShowerKit.fbx` / `Runtime/Audio/ShowerLoop.wav` へ上書きコピーする(`.meta` はそのまま。GUID とインポート設定を保つ)。
4. Unity で再インポートし、`ShowerKit.prefab` を開いてメッシュ・マテリアルが外れていないことを確認する。
5. `Assets/garakshop/shower_kit/` に作った一時ファイルは消す(開発リポジトリにはコミットしない)。
