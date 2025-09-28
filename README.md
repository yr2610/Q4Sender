# Q4Sender

Q4Sender は QR コードを連続表示するための Windows フォームアプリケーションです。任意のテキストやファイルを QR コード列に変換し、タイマーに合わせて表示できます。

## 設定ファイル

QR コードの誤り訂正レベルやバージョンを調整したい場合は、実行ファイルと同じフォルダーに `conf.yaml` を配置してください。詳細な書き方は [docs/conf.md](docs/conf.md) を参照してください。

## 開発メモ

- 対応フレームワーク: .NET 8.0 (Windows フォーム)
- 主要依存関係: [QRCoder](https://github.com/codebude/QRCoder)、[YamlDotNet](https://github.com/aaubry/YamlDotNet)
