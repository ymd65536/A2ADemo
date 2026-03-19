# Multi-Agent Evaluation System

## Overview

複数の評価エージェントを作成してKubernetes上で動かすためのシステムです。評価エージェントは、Azure AI Evaluation SDKを使用して、Microsoft Agent Frameworkをベースに構築されます。これらのエージェントは、A2A dotnetを利用して、様々なタスクやシナリオに対して評価を行います。また、個々のエージェントはKubernetes上のサービス単位で独立して動作し、他のシステムからA2Aで呼び出すことができます。

## SDK

- Azure AI Evaluation SDK
- Microsoft Agent Framework
- A2A dotnet

## System Architecture

3つのエージェントがKubernetes上で動作し、各エージェントは独立したサービスとして提供されます。これらのエージェントは、A2A dotnetを通じて呼び出され、評価タスクを実行します。

1. **Violence Evaluator**: 暴力的なコンテンツを評価するエージェント。
2. **Sexual Evaluator**: 性的なコンテンツを評価するエージェント。
3. **chatbot**: チャットボットエージェント。ユーザーとの対話を通じて、様々なタスクやシナリオに対して評価を行います。応答内容はいったん各Evaluatorを呼び出してから返す形であり、Evaluatorを呼び出すかどうかはユーザーからの質問内容によって判断されます。

3つのエージェントとは別にA2AのクライアントもKubernetes上で動作し、これらのエージェントを呼び出すことができるフロントエンドとして機能します。
