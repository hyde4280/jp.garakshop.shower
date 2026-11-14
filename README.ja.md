# Garak's Shop Shower

[English](README.md)

VRChat ワールド向けのシャワーギミックです。UdonSharp で書かれています。

- シャワーヘッド (VRC Pickup) を掴んでホルダーから外せる
- ヘッドに追従するホース
- 水しぶきのパーティクル
- 流水音

## 動作要件

- Unity 2022.3
- VRChat Worlds SDK (`com.vrchat.worlds`) 3.10.5 以降 (3.x)

## インストール

> Garak's Shop VPM リスティングで公開中です。以下の手順で VCC にリスティングを追加し、プロジェクトへパッケージを追加してください。

1. VRChat Creator Companion (VCC) を開く。
2. リポジトリリスティングを追加する: `https://hyde4280.github.io/garakshop-vpm/index.json`
3. ワールドのプロジェクトに **Garak's Shop Shower** を追加する。

## 内容

```
jp.garakshop.shower/
  package.json
  README.md
  CHANGELOG.md
  LICENSE
  Runtime/
    GarakShop.Shower.asmdef   スクリプトのアセンブリ定義
    GarakShop.Shower.asset    UdonSharp アセンブリ定義
    Scripts/                  UdonSharp スクリプト
    Meshes/                   メッシュ
    Materials/                マテリアル
    Prefabs/                  プレハブ (ShowerKit)
    Audio/                    サウンドクリップ
  Samples~/                   Shower Sample (Package Manager の Samples からインポート)
  Tools~/                     生成ツール (エディタ専用)
```

## 使い方

スクリプトはすべて `GarakShop.Shower` 名前空間にあります。これらを使う・継承する自作スクリプト (例: `ShowerRippleReceiver` のサブクラス) には `using GarakShop.Shower;` が必要で、そのアセンブリは `GarakShop.Shower` を参照しなければなりません。

## サンプル

パッケージの *Samples* セクション (Unity Package Manager ウィンドウ、または Window > Package Manager の一覧にあるこのパッケージの項目) から **Shower Sample** をインポートし、`ShowerSample.unity` を開いてください。床・壁・スポーン1つの `VRCSceneDescriptor` と、ワールド原点に置いた `ShowerKit` プレハブ1つが入っています (シャワーの座標はワールド座標なので、プレハブルートは原点・無回転のままにしてください)。`ShowerSplash.ripples` は空にしてあるため、浴槽の水面はありません。ライティングのベイクやスカイボックスは無く、シャワーが動作することの確認専用シーンです。

## ライセンス

MIT License。[LICENSE](LICENSE) を参照してください。
