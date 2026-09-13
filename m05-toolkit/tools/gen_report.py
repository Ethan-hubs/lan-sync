#!/usr/bin/env python3
'''生成 M0.5 报告骨架（手册 §12）——含版本锁定表与回填清单，直接往里填数字。

用法：
  python3 gen_report.py --out M05-报告.md
  python3 gen_report.py --out M05-报告.md --tag v2.1.5 --commit 2ca95cf1498104113fdfde46df4107f2450a0f71 \\
      --exe-sha <SHA256> --disco-image <ID> --relay-image <ID>
'''
import argparse
from datetime import datetime, timezone


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="M05-报告.md")
    ap.add_argument("--tag", default="v2.1.5")
    ap.add_argument("--commit", default="2ca95cf1498104113fdfde46df4107f2450a0f71")
    ap.add_argument("--exe-sha", default="")
    ap.add_argument("--zip-sha", default="")
    ap.add_argument("--disco-image", default="")
    ap.add_argument("--relay-image", default="")
    a = ap.parse_args()

    now = datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds")
    doc = f"""# M0.5 技术基线 PoC 报告

> 生成时间：{now}（骨架由 gen_report.py 生成，请填入实测值）
> 配套：《局域网同步系统-产品文档-v0.9(rc)》《M0.5 技术基线 PoC 操作手册 v0.2》

## 1. 总结

```text
执行日期：
执行人：
PRD：v0.9 rc
Syncthing：{a.tag}
Commit：{a.commit}
ECS：<YOUR_ECS_IP>（出口 IP：____）
结论：PASS / CONDITIONAL PASS / FAIL
是否允许冻结 v1.0：YES / NO
```

## 2. 版本锁定表

| 项 | 实测值 | PASS |
|---|---|---|
| Syncthing version | {a.tag} |  |
| Source tag | {a.tag} |  |
| Source commit | {a.commit} |  |
| Windows amd64 ZIP SHA-256 | {a.zip_sha or '<填>'} |  |
| `syncthing.exe` SHA-256 | {a.exe_sha or '<填>'} |  |
| `stdiscosrv` image ID | {a.disco_image or '<填>'} |  |
| `strelaysrv` image ID | {a.relay_image or '<填>'} |  |
| A/B/C/D 版本与 hash 一致 |  |  |
| 自动升级关闭（`--no-upgrade`） |  |  |

**升级策略**：阶段一不自动升级；仅在安全修复或明确需要上游功能时提升级；升级前重跑「基础同步 + E3 + E5 + E6 + relay 回落」回归。

## 3. 复现所需的 ID

```text
DISCO_SERVER_ID   = ______
RELAY_DEVICE_ID   = ______
DEVICE_A_ID       = ______
DEVICE_B_ID       = ______
DEVICE_C_ID       = ______
DEVICE_D_ID       = ______
FOLDER_ID         = ______
RELAY_TOKEN       = TOKEN_SET=yes（不写内容）
```

## 4. 五项基础能力

| 项 | 结果 | 证据路径 | 备注 |
|---|---|---|---|
| 双机同步 | | | |
| 自建 stdiscosrv | | | |
| 私有 relay + token（含"两层准入"验证） | | | |
| LAN direct / WAN fallback | | | |
| REST + version restore | | | |

## 5. E2–E6

| 实验 | 关键数字 | PASS/FAIL | 回填章节 |
|---|---|---|---|
| E2 | 安全实测并发、峰值内存、双向/单向吞吐 | | §13 / §15 |
| E3 | 语义用例通过数 / 失败项（含 verify_tree.py 结果） | | §15 / §18 |
| E4 | 直连率 p、`bytesProxied` 与客户端字节偏差 | | §13 |
| E5 | 四类操作 P50/P95（delay=10 与调优后） | | §11 / §18 |
| E6 | 五场景恢复 P50/P95 | | §11 / §18 |

## 6. §13 回填（成本）

```text
ECS 实测 relay 吞吐（双向）＝ ___ Mbps  → 单向 ≈ ___ Mbps
观测跨网直连率 p ＝ ___
按实测 p 预计月中继出流量（出网单向）＝ ___ GB
全中继悲观上界 ＝ ___ GB
预计月流量费（0.8 元/GB × 出网单向）＝ ___ 元
relay 1/2/3 session 峰值内存 ＝ ___ / ___ / ___ MiB
是否发现长传内存单调上涨 ＝ YES / NO
```

## 7. §14 回填（工期与门槛）

```text
M0.5 ＝ PASS / CONDITIONAL / FAIL
M1 准入 G1/G2/G4 ＝ ___
阶段一 2–4 周估时是否仍成立 ＝ YES / NO
新增阻塞项 ＝ ___
```

## 8. §15 回填（风险）

```text
ECS OOM 风险：高/中/低，依据 ___
Watcher 时延风险：高/中/低，依据 ___
网络切换风险：高/中/低，依据 ___
Relay 成本风险：高/中/低，依据 ___
Windows 文件语义风险：高/中/低，依据 ___
```

## 9. 冻结 v1.0 的 Gate 勾选

- [ ] 四机版本与二进制 hash 一致
- [ ] 两个服务端组件同 tag/commit
- [ ] 官方 discovery / public relay 已关闭
- [ ] 私有 discovery 生效
- [ ] 私有 relay 正确 token 可用、错误 token 被拒
- [ ] 未互加 Device ID 的设备即使持 token 也看不到文件
- [ ] LAN direct 成功
- [ ] WAN direct / relay fallback 成功
- [ ] REST 满足 Adapter 基线
- [ ] E2/E3/E4/E5/E6 完成并回填
- [ ] §13/§14/§15 已回填
- [ ] 版本锁定表已确认
- [ ] G1 Adapter 接口一页设计完成
- [ ] G2 E2EE/设备入网一页流程完成
"""
    with open(a.out, "w", encoding="utf-8") as fh:
        fh.write(doc)
    print(f"[ok] 报告骨架：{a.out}")


if __name__ == "__main__":
    main()
