# 差分语料

命名：`<case>.input.json` / `<case>.expected.json`。

- 输入约定：文件里的 `payload` 键会被序列化后送入被测程序 stdin；没有 `payload` 则整个文件原样送入
- `.expected.json` 只能由参照实现生成：`diff_corpus.py record`
- 每条语料的名字要说明它在守什么
- 参照实现升级时先重录、再逐条看差异决策，不要一把 `--update` 全过
