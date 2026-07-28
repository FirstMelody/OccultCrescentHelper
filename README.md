# BOCCHI 北征开发版

> ⚠️ **极度不稳定，随时更新，谨慎使用。**

这是 [OhKannaDuh/BOCCHI](https://github.com/OhKannaDuh/BOCCHI) 的实验性开发分支，
主要用于提前适配《蜃景幻界：新月岛 北征之章》，并研究 Forked Tower 地图机制。
它可能崩溃、误判或在游戏更新后立即失效；请自行承担使用风险。

## Dalamud 安装

在 Dalamud 设置的“测试版”/“自定义插件仓库”中加入：

```text
https://raw.githubusercontent.com/FirstMelody/OccultCrescentHelper/master/repo.json
```

保存后，在插件安装器中搜索 `BOCCHI`。仓库会从 GitHub Release 的 `latest.zip`
安装并接收更新。

## 当前开发功能

- 北征之章 Territory/Map 强制绑定与 dev 地图采集
- 铜/银宝箱、胡萝卜、罐子宝箱和调查地点自动标记
- 自动统计附近连续静止且未进入交战的怪物，并按怪物类型与生成区域聚合为中心点
- FATE/CE 坐标和本地化名称记录
- Eureka Linker 风格的地图图标与分类显示开关；采集点位只读
- Forked Tower EventObj 采集、雷点编组、互斥排除和 3D 机制范围
- 动态北征 Illegal Mode 支持
- 北岛已共鸣魔路采集，以及按 vnav 实际路径选择直走或 Lifestream 传送
- CE/FATE 完成后返回自定义蹲守点
- BossMod AI 自动切换默认关闭，可由用户显式开启

常用指令：

```text
/bocchi dev on
/bocchi dev off
/bocchi dev bind
/bocchi dev tower
/bocchi dev tower-auto
/bocchi telemetry on|off|status
```

## 匿名地图遥测

插件首次运行或上传范围扩大时会询问是否上传地图资料。**只有明确点击同意后才会上传**，
拒绝后不会静默上传。上传内容仅限游戏内容坐标：

- Territory/Map、标记类型、XYZ
- FATE/CE Event ID 与游戏内名称
- 调查地点 Base ID、名称与坐标
- 静止未交战怪物的 Base ID、名称、等级与坐标
- Tower EventObj Base ID、类型、Hitbox/机制半径

不会上传角色名、Content ID、账号、服务器、聊天内容或玩家实时位置。可随时执行
`/bocchi telemetry off` 关闭。

为防止公开地图被恶意数据污染，服务端会使用仅存于服务器的秘密密钥，对请求网络
地址生成不可逆 HMAC 哈希。该哈希不公开，仅用于限流、审计和整批撤销对应来源的
贡献；原始网络地址不写入标记数据库。

公开聚合数据面板：
[BOCCHI 地图遥测](https://h.lionwebsite.xyz/bocchi-telemetry/)

服务端源码位于 [`TelemetryServer`](./TelemetryServer)。

## 构建与发布

本地需要 Dalamud API 15 开发程序集：

```powershell
dotnet build .\BOCCHI.sln -c Release
```

推送形如 `v3.2.0` 的 tag 会触发 GitHub Action，构建插件并创建包含 `latest.zip`
与 SHA-256 校验文件的 Release。

## 许可证与致谢

本项目沿用 AGPL-3.0-or-later。感谢 BOCCHI、Ocelot、Pictomancy、
EurekaTrackerAutoPopper/KamiToolKit 的原作者与贡献者。
