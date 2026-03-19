# Multi-Agent Evaluation System

## Overview

複数の評価エージェントを作成してKubernetes上で動かすためのシステムです。評価エージェントは、Azure AI Evaluation SDKを使用して、Microsoft Agent Frameworkをベースに構築されます。これらのエージェントは、A2A dotnetを利用して、様々なタスクやシナリオに対して評価を行います。また、個々のエージェントはKubernetes上のサービス単位で独立して動作し、他のシステムからA2Aで呼び出すことができます。

## SDK

- Azure AI Evaluation SDK
- Microsoft Agent Framework
- A2A dotnet
