# KardsCardScanner

基于旁边的 CUE4Parse 源码构建的独立 CLI 工具，用于扫描 Pak/IoStore 中：

`kards/Content/Blueprints/Cards/` 下名称以 `card_` 开头的 `.uasset`。

工具会尝试读取 BlueprintGeneratedClass 的 Class Default Object，并把 `Name`、`Rarity`、`CardSet`、`Type`、来源路径及可识别的基础属性写入 JSON。未识别字段不会丢弃，而是保存在每张卡的 `Properties` 中，方便根据实际资产继续补充候选字段。

## 运行

在本目录执行：

```text
dotnet run
```

无参数会显示交互式 CLI，可依次选择 Pak/游戏目录、usmap、UE 版本、AES Key 和输出路径。

也支持无交互参数：

```text
dotnet run -- --source "D:\KARDS\default\game\kards" --ue UE5.3 --output cards.json
```

参数：

```text
--source <路径>       Pak 文件、Pak 目录或游戏目录
--mapping <路径>      .usmap 映射文件，可省略
--ue <版本>           GAME_UE5_3、UE5.3 等
--aes-key <密钥>      AES Key，可省略
--output <路径>       输出 JSON，默认 cards.json
```

首次读取 Oodle/Zlib 压缩资产时，CUE4Parse 可能需要初始化或下载相应运行库；加密归档还必须提供正确 AES Key。
