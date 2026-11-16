# §242 cron 的可选秒/年字段与描述符

## 背景

C260：参照使用 aptible/supercronic 的 cronexpr（gorhill 分支），支持可选秒
字段、可选 1970-2099 年字段与 predefined descriptors；端口只接受 5 字段。

## 实现

- `GcCronSchedule`
  - 支持 5/6/7 字段：5 字段秒=0、年通配；6 字段秒在前；7 字段年最后
    （1970-2099）。
  - 支持 `@yearly` / `@annually` / `@monthly` / `@weekly` / `@daily` /
    `@midnight` / `@hourly`（按 supercronic 的七字段展开）。
  - `NextOccurrence` 在秒级选择候选；year 字段限制匹配；无 year 字段仍保留
    8 年上限（覆盖闰日）。
  - 标准 5 字段文法与 Vixie day-of-month/day-of-week 语义不变。
- `L/W/#` 日期级语法（最后一天、最近工作日、第 n 个星期几）仍不支持，
  拆到 C309 accepted。

## 测试

- `GcCronScheduleTests` 新增：6 字段秒步进、6 字段补零秒、@yearly、@hourly、
  year 字段限制、L/W/# 拒绝；原 6 字段“非法”用例改为合法。
- 定向 `GcCronScheduleTests|ChannelAutoDisableTests`：80/80。
- 未跑 Api 全量。

## 台账

- C260 → resolved；C309（L/W/#）accepted。
- 309 条（193 accepted / 116 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 已重新生成。
