# AutoMobFarm

FINAL FANTASY XIV の Dalamud プラグイン。素材と目標の所持数を選ぶと、狩り場へ行き、その素材を落とす通常フィールドの敵を倒して集める。

## できること

| タブ | 内容 |
|---|---|
| モブ | 素材を落とすフィールドの敵の一覧（ドロップ品・座標・最寄りのエーテライト・レベル・拡張・ラーニング・とらえる可否）。座標を押すと、その地点の敵を倒し続ける「その場の討伐」を始める。右クリックで自動討伐に追加する |
| アイテム | レベル帯ごとの素材と、代表の座標・敵。帯を右クリックすると、その帯の素材のプリセットを作る |
| 自動討伐 | プリセットに品と目標数を並べ、「開始」で全部のプリセットの有効な品を上から順に集める。見出しの下の一括の行で、有効と目標数をまとめて変えられる（収集中に変えた目標数もすぐ効く）。Artisan の Crafting Lists から、敵が落とす素材だけのプリセットを作れる |
| 必須プラグイン | 使うプラグインと状態。足りなければ「インストール」（配布元の登録からインストールまで）、止まっていれば「有効にする」。使えるものにはボタンを出さない |

## 動き

- 移動中は Rotation Solver Reborn（RSR）を Off、戦う瞬間は Henched（狙った敵だけを殴る）にする。戦っている間は BossMod Reborn の AI に足（範囲攻撃の回避・相手へ寄る）だけを任せ、技とターゲットは任せない。終えたら RSR と BossMod Reborn の設定を元に戻す。
- こちらから仕掛けるのは、カタログの地点から水平 60m 以内で、名前・基本 ID・個体の判定に合う敵だけ。FATE・クエスト・リーヴ・判別できない敵・他人の獲物には仕掛けない。攻撃してきた敵には、計画外でも反撃する。
- 同じ地点を 3 回見回っても見つからなければ、同じ品の別エリアへ、無ければ次の品へ進む。戦闘不能・コンテンツ参加中などは条件待ちになり、条件が戻れば再開する（帰還・蘇生の受諾は自動では行わない）。
- 収集中に見かけた敵のレベルを記録し、カタログでレベルが分からない敵の表示に使う。

## 導入

`/xlsettings` で Dalamud の設定を開き、「Experimental」タブの「Custom Plugin Repositories」に次の URL を足して保存する（日本語表示では項目名が違うことがある）。そのあとプラグインの一覧（`/xlplugins`）から「AutoMobFarm」を入れる。

```
https://raw.githubusercontent.com/Tashi-sysins/Tashi-sysins-repo/main/repo.json
```

## 必要なプラグイン

主画面の「必須プラグイン」タブで状態を確かめ、足りなければそのまま入れられる。

| プラグイン | どのくらい要るか | 使う場面 |
|---|---|---|
| vnavmesh | **必須** | 狩り場の中の移動・敵へ近づく経路（設定の「Auto load mesh」を ON にする） |
| Rotation Solver Reborn | **必須** | 戦闘（技を自動で撃つ） |
| Lifestream | 推奨 | 狩り場のエリアへのテレポ（無ければ手で移動する） |
| BossMod Reborn | 推奨 | 範囲攻撃の回避・相手へ寄る（無くても集められる。本家の BossMod と一緒に読み込んでいると使わない） |

## コマンド

| コマンド | 内容 |
|---|---|
| `/amf` | 素材収集の画面を開く |
| `/amf admin` | 管理画面（環境・記録）を開く |
| `/amf stop` | 止める（RSR は Off のまま。元に戻すのは画面のボタン） |

## 設定と記録

`%APPDATA%\XIVLauncher\pluginConfigs\AutoMobFarm\` に、設定・見つけた位置（`found_spots.jsonl`）・見かけた敵（`seen_mobs.jsonl`）・入手ログ（`drops\`）・動作の記録（`logs\`）を保存する。

## データの出どころ

敵の出現位置・落とす品・レベル・青魔法のラーニング・魔獣使いの捕獲は、埋め込みのモブ用カタログ（`AutoMobFarm/Data/mob_catalog_pack.json`）から読む。出典と条件は [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。出現位置は候補であって攻撃の許可ではなく、仕掛けるかは現地の判定で決める。

## ビルド

```
git clone --recursive https://github.com/Tashi-sysins/AutoMobFarm.git
cd AutoMobFarm
dotnet build AutoMobFarm/AutoMobFarm.csproj -c Release
```

- Dalamud.NET.Sdk 15（Dalamud API 15）。
- 画面は UI ライブラリ EstellUtils で描く（サブモジュール `EstellUtils`。clone のときに `--recursive` を付けるか、あとで `git submodule update --init`）。
- `-p:DeployDir=<フォルダー>` を付けると、Release ビルドの後にそのフォルダーへ写す（付けなければ写さない）。

## ライセンス

[AGPL-3.0](LICENSE)。埋め込みカタログには GPL-3.0 の資料（LuminaSupplemental）を含む。第三者の資料と部品は [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 開発者

ヌル・Estell
