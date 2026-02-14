## A2A Demo

A2Aで動かすまえにJSON RPCとKubernetesを使用して通信する方法を説明します。
そのあと、Microsoft Agent FrameworkとA2Aで動かす方法を説明します。

## Step1: JSON RPCとKubernetesを使用して通信

このステップを完了するとKubernetesクラスターとJSON RPCを使用して通信できます。

まずはコンテナイメージをビルドします。WeatherAgentとOrchestratorの両方をビルドしてください。

```bash
cd WeatherAgent
docker build -t a2a-weather:net10 .
cd ../Orchestrator
docker build -t a2a-orchestrator:net10 .
```

次に、kubectlを使用してクラスターにapplyします。
k8s/a2a-deploy.yamlを使用して、両方のコンテナイメージをクラスターにデプロイしてください。

```bash
cd ..
kubectl apply -f k8s/a2a-deploy.yaml
```

クラスターが起動したら、Orchestratorのサービスを確認します。

```bash
curl "http://localhost:30001/ask?tool=get_weather"
# [.NET 10 Orchestrator] Result: {"jsonrpc":"2.0","result":"現在の東京は .NET 10 のように爽やかな快晴です。","id":"179da644-271c-462f-aa3f-04e939b8e780"}%  
```

## wip Step2: Microsoft Agent FrameworkとA2Aで動かす方法

```bash
cd A2AServer
docker build -t a2a-a2a-server:net10 .
```

```bash
cd A2AClient
docker build -t a2a-orch-a2a-client:net10 .
```

```bash
cd k8s
kubectl apply -f a2a-client-server.yaml
```

```bash
curl -G "http://localhost:30001/ask" --data-urlencode "text=こんにちは" -v
```

## memo: kubectl

```bash
kubectl get pods
```

```bash
kubectl rollout restart deployment a2a-server
```

```bash
kubectl rollout restart deployment orchestrator-a2a-client
```

```bash
kubectl logs orchestrator-a2a-client-6b6448f696-mrxck
```

## memo

`OrchestratorAgent`は`AgentSample`のOrchestrator、`WeatherAgent`は`AgentSample`のWeatherAgentです。
どちらもJSON RPCを使用して通信します。
