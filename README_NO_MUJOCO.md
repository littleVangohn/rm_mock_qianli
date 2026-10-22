# RoboMaster 模拟器


## 怎么跑

1. 用 Unity 6000.0.26f1 打开本目录。
2. 打开 `Assets/Scenes/Welcome.unity`（构建列表第一条已改为 Welcome）。
3. Play，选择模式（Training Ground / RMUC2024 / RMUC2023）。
4. 进 Multiplayer → **Host**，然后选车、Spawn。
5. 打包：File → Build Settings → Windows，直接 Build。玩家机器不需要 Unity，也不需要后端。

## WSL 里直接运行发布包

```bash
bash /mnt/d/Pacific_doorlock/_oss_reference/no_mujoco_mock/run-no-mujoco.sh
```
![alt text](image-1.png)
![alt text](image-2.png)