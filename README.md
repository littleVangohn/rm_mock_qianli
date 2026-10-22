# Robomaster Simulator

## Introduction

This is a simulator built with unity aim to provide a tool for the Robomaster Teams to develop their strategy and find out the developing directions for their robots. Looking forward to any contributors.

## Project Structure

Need a new wiki to explain all of these.

![project structure](/Doc/project-structure.png)

## Features

### 1. Game Simulation

## Match
  - RMUC2023 (95%)
  - RMUC2024 (80%)
  - RMUC2025 (WIP)

## Mine & Exchange

## Activate Buff

### 2. Control Simulation

## Different Wheel
  - Mecanum wheel
  - Omni wheel
  - Steering wheel

## Power Consumption

### 3. Algorithm Test

## Camera Stream for ROS

## MuJoCo Backend (integration branch)

An opt-in adapter for the external `pacific-doorlock.realtime.v2` MuJoCo backend
is available under `Assets/Scripts/MujocoBackend`. It does not replace the
existing Unity/Netcode authority automatically; setup and migration boundaries
are documented in [Doc/MUJOCO_BACKEND.md](Doc/MUJOCO_BACKEND.md). Start the
whole stack (WSL backend + Unity) from `D:\Pacific_doorlock` with
`sh run-mujoco.sh`; that is the single launch entry.


## Requirements

- Unity Editor 6000.0.26f1
- PATCH - Updating System Basic V2.6.7
