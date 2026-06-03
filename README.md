# SMT-EVEData

SMT 项目的数据层，提供星系地图、BFS 导航、ZKB 引擎、ESI 工具类

从 [SMT (Slazanger's Eve Map Tool)](https://github.com/Slazanger/SMT) 中提取的独立 NETSDK 项目，被 [SMTAlert](https://github.com/yuruichang/SMTAlert) 引用。

## 技术栈

NETSDK 项目（.NET 8.0）— 需要 Visual Studio 2022+ 编译

- 星系地图数据（约 8000+ 星系、95 个星域）
- BFS 多跳路径导航
- ZKB RedisQ 击杀推送引擎
- ESI 工具类（SSO 授权、角色/公司/联盟信息获取）
- Intel 频道解析引擎
- 角色/联盟声望管理

## 使用

供 SMTAlert 作为项目引用使用。搭建方式见 SMTAlert 的 README。
