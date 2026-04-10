## Overview

## uvのセットアップ

```bash
curl -LsSf https://astral.sh/uv/install.sh | sh
```

インストールできたらバージョンを確認します。

```bash
uv --version 
# uv 0.9.18
```

仮想環境を作成します。

```bash
uv venv .venv
```

仮想環境をアクティベートします。

```bash
source .venv/bin/activate
```

必要なパッケージをインストールします。

```bash
uv pip install -r requirements.txt
```

インストールできたか確認します。

```bash
python -c "import a2a; print('A2A SDK imported successfully')"
```

バージョンを確認します。

```bash
python --version
# Python 3.12.1
```

## install gcloud

First, install gcloud using curl.

```bash
curl -sSL https://sdk.cloud.google.com | bash && exec -l $SHELL && gcloud init
```

Then, log in with gcloud.

```bash
gcloud auth login
```

You can find your Project ID on the Google Cloud Welcome page.
※Check [welcome google cloud](https://console.cloud.google.com/welcome?)

The correct command is:
```bash
gcloud config set project PROJECT_ID
```

For example, if your project ID is `my-awesome-project-123`, you would run:
```bash
gcloud config set project my-awesome-project-123
```

This command sets the active Google Cloud project for all subsequent `gcloud` commands.

Next, you will set up Application Default Credentials (ADC).

```bash
gcloud auth application-default login
```

This completes the setup. / The setup is now complete.

### Google Cloud Project ID

If you are not using a `.env` file in this project, set the following environment variables directly in your terminal before running the Slack app.
If you are using a `.env` file, you can skip this step as the `PROJECT_ID` will be set automatically when you run the app.

Set the `PROJECT_ID` environment variables.

```bash
export PROJECT_ID=`gcloud config list --format 'value(core.project)'` && echo $PROJECT_ID
```

## 参考

- [python-environment-sdk-installation](https://a2a-protocol.org/latest/tutorials/python/2-setup/#python-environment-sdk-installation)
- [google-22-full](https://console.cloud.google.com/artifacts/docker/serverless-runtimes/us-central1/google-22-full)
- [a2a sample](https://github.com/a2aproject/a2a-samples/tree/main/samples/python/agents/helloworld)
