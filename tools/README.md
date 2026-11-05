# tools/ —— 可选的外部工具

## PawnIO_setup.exe

**用途**：CPU 温度要读 MSR，必须有 ring0 驱动。LHM 0.9.5 之后改用签名驱动 PawnIO，本程序启动时若注册表里没有 `PawnIO` 服务，会静默执行：

```
PawnIO_setup.exe -install -silent
```

装完重新 `Open()` 一次传感器（LHM 在启动时缓存 MSR 可用性，不重开不生效）。

**获取**：从 <https://github.com/namazso/PawnIO> 的 Releases 下载 `PawnIO_setup.exe`，放到本目录。`build.bat` 会把它一起复制到 `bin\Release\`。

**不入库的原因**：3.3 MB 的第三方安装包，且是独立签名的驱动安装程序，应由使用者自行从官方渠道获取，避免二手分发被篡改。

**没有它也能跑**：`EnsureDriver()` 会跳过安装并在托盘菜单第一行标注 `· 需安装 PawnIO`，此时 CPU 温度与硬盘温度为 `null`，其余指标正常。

## 权限说明

程序清单声明了 `requireAdministrator`，双击会弹 UAC。需要管理员的只有三件事：

1. 安装 / 读取 PawnIO 驱动（CPU、硬盘温度）
2. 读硬盘 SMART 与真实型号
3. 自动添加防火墙入站规则 `HwMonitor`（TCP 9455 / UDP 9456，`profile=any`）

非管理员下运行不会崩溃，只是温度显示 `--`，且需要手动放行防火墙。
