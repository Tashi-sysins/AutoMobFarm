# 第三者の資料と部品

## 埋め込みのモブ用カタログ

`AutoMobFarm/Data/mob_catalog_pack.json` は、次の資料から作った派生データを含む。資料ごとに条件が違うので、すべてを同じライセンスと見なさないこと。

### 1. ffxiv-teamcraft（monsters.json：出現位置の候補と戦闘レベル）

https://github.com/ffxiv-teamcraft/ffxiv-teamcraft （コミット 53405826790e1405831003a75935d9eb66c2dd0a）

```
MIT License

Copyright (c) 2017 Flavien Normand

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### 2. LuminaSupplemental.Excel 4.3.3（MobDrop・MobSpawn・BNpcLink：落とす品・出現地点・敵の対応）

https://github.com/Critical-Impact/LuminaSupplemental

GNU General Public License v3.0。全文は [licenses/GPL-3.0.txt](licenses/GPL-3.0.txt)。
この資料を含むので、AutoMobFarm は GPL-3.0 と組み合わせられる AGPL-3.0 で公開している。

### 3. Garland Tools（敵のレベルの範囲：Teamcraft にレベルが無い敵の参考）

https://www.garlandtools.org/ （ソース https://github.com/ufx/GarlandTools）

```
MIT License

Copyright (c) 2018 Chris Faulhaber

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### 4. BeastHelper（魔獣使いの捕獲：どの番号の魔獣をどの敵から捕まえるか）

https://github.com/Rin-0617/BeastHelper （コミット 7dce675）

ライセンスの表示なし。事実の対応だけを使い、ソースは含めていない。番号・種類・絵はゲームデータ（XBMPet・Pet）から読んでいる。

### 5. ゲームエイト「【FF14】青魔法ラーニングの修得条件一覧」（青魔法：どの敵からどの技を覚えるか）

https://game8.jp/ff14/297123

事実の対応だけを使い、文章は含めていない。技の番号は、ゲームの青魔法の番号（AozActionTransient の Number）と同じもの。

### 6. FINAL FANTASY XIV のゲームデータ（地域・地図・名前・アイコンの番号）

© SQUARE ENIX CO., LTD. ゲームデータの権利は権利者にある。

## 部品

### EstellUtils（画面の UI ライブラリ）

https://github.com/rioriopu/EstellUtils

サブモジュール `EstellUtils` として参照し、配布の zip に `EstellUtils.dll` を含める。
