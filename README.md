# Q4Sender

Q4Sender は QR コードを連続表示するための Windows フォームアプリケーションです。任意のテキストやファイルを QR コード列に変換し、タイマーに合わせて表示できます。

QRScanner と組み合わせると、PC の画面に表示した QR コード列を Android などのカメラで読み取り、元ファイルを復元できます。

## 基本的な使い方

1. Q4Sender を Windows で起動します。
2. `Mode` は通常 `Fountain` のまま使います。
3. 送信したいファイルを開きます。
4. QR コードの表示速度や設定を必要に応じて調整します。
5. QRScanner 側で同じモードを選び、読み取りを開始します。

## 転送モード

### Fountain

標準モードです。Wirehair ベースの `Q4W` フレームを使います。

読み取り側はすべての QR コードを順番通りに読む必要はありません。十分な数の異なるフレームを読めると復元できます。読み逃しに強く、通常はこちらを使います。

Fountain 生成には同梱の JavaScript ヘルパーを使うため、Windows に Node.js がインストールされていて、`node` コマンドが実行できる必要があります。

### Legacy Q4

従来方式です。`Q4|...` フレームを固定順序で送ります。

すべての断片を集める必要があります。SkipCode32 による不足範囲の確認や比較用として残しています。

## Scanner

Scanner は submodule として `Scanner/` に入っています。

QRScanner 側の README には、読み取り方法、保存・共有ルール、注意点を書いています。

## ファイルの扱い

送信時はファイルを単一ファイル ZIP に包んでから QR コード化します。

Scanner 側では復元後、Android で扱いやすい一部の拡張子だけ元ファイルとして保存・共有し、それ以外は ZIP のまま保存・共有します。

元ファイルとして扱う拡張子:

- `.jpg`
- `.jpeg`
- `.png`
- `.gif`
- `.webp`
- `.txt`
- `.pdf`
- `.mp4`
- `.zip`

上記以外、たとえば `.cs` や `.js` などのコードファイルは ZIP のまま保存・共有されます。Android の共有先アプリによって扱えるファイル種別が違うため、安全側に倒しています。

元ファイルが最初から `.zip` の場合は、ZIP をさらに ZIP に包んだままにはせず、中の ZIP を取り出してそのまま保存・共有します。

## 計測

QRScanner は最初の有効な QR フレームを読んだ時点から計測を始め、読み取り中から所要時間、容量目安、秒あたりの容量を表示します。復元完了後は実際に復元した容量で表示します。Fountain と Legacy の比較に使えます。

## 設定ファイル

QR コードの誤り訂正レベルやバージョンを調整したい場合は、実行ファイルと同じフォルダーに `conf.yaml` を配置してください。詳細な書き方は [docs/conf.md](docs/conf.md) を参照してください。

## 開発メモ

- 対応フレームワーク: .NET 8.0 (Windows フォーム)
- 主要依存関係: [QRCoder](https://github.com/codebude/QRCoder)、[YamlDotNet](https://github.com/aaubry/YamlDotNet)
- Fountain 生成: `Scanner/libs/wirehair-wasm` と `Sender/Tools/wirehair-encode.mjs`
- プロトコル概要: [docs/q4f-protocol.md](docs/q4f-protocol.md)
