# 识别校准基准（2026-09-10）

这批基准来自真实购物阶段截图 `logs/live-frames/20260907-214047-733-Shopping.png`。
我已将商店槽位裁剪图与当前国服卡库 `data/catalog/cards` 中的原图逐张比对。

## 待确认的正确样本

| 槽位 | 当前识别 ID | 卡牌 | 当前置信度 | 裁剪图 | 卡库原图 |
| ---: | ---: | --- | ---: | --- | --- |
| 0 | `80740` | 贝类收藏家 | 88.2% | `logs/calibration-baseline/20260907-214047-733-Shopping/shop-slot-0.png` | `data/catalog/cards/80740.png` |
| 1 | `130658` | 深渊打手 | 87.8% | `logs/calibration-baseline/20260907-214047-733-Shopping/shop-slot-1.png` | `data/catalog/cards/130658.png` |
| 2 | `105799` | 兽人指挥 | 88.5% | `logs/calibration-baseline/20260907-214047-733-Shopping/shop-slot-2.png` | `data/catalog/cards/105799.png` |
| 3 | `116182` | 坑谷矿工 | 90.7% | `logs/calibration-baseline/20260907-214047-733-Shopping/shop-slot-3.png` | `data/catalog/cards/116182.png` |
| 4 | `80747` | 熔岩潜伏者 | 90.7% | `logs/calibration-baseline/20260907-214047-733-Shopping/shop-slot-4.png` | `data/catalog/cards/80747.png` |

槽位 5 当前为 `UNKNOWN`（38.7%），不作为正确样本，也不参与本轮校准。

## 校准规则

- 只有用户确认正确的槽位才进入正样本。
- 校准先调整卡牌缩略图裁剪、特征权重和置信度/第二名差值，再重新跑其他购物截图。
- 新卡库上线后，先自动为新增和图片变化的卡牌建立索引；只有无法稳定区分的候选才进入待确认列表。
- 这批基准尚未自动写入长期正样本库，避免在用户确认前污染识别结果。
