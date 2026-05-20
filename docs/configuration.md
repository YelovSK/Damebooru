# Configuration

Damebooru uses standard ASP.NET Core configuration binding.

The canonical configuration shape lives in `server/Damebooru.Core/Config/DamebooruConfig.cs`.

Configuration can be supplied through:
- `server/Damebooru.Server/appsettings.json`
- `server/Damebooru.Server/appsettings.{Environment}.json`
- environment variables
- Docker Compose environment blocks / secrets

## Environment Variable Naming

The root configuration section is `Damebooru`.

Nested values use `__` in environment variables.

Examples:
- `Damebooru:Storage:DatabasePath` -> `Damebooru__Storage__DatabasePath`
- `Damebooru:Scanner:EnableWatcher` -> `Damebooru__Scanner__EnableWatcher`
- `Damebooru:ExternalApis:SauceNao:ApiKey` -> `Damebooru__ExternalApis__SauceNao__ApiKey`

## Common Notes

- Relative storage paths are resolved from `server/Damebooru.Server` in local runs.
- Docker secret files can be mapped directly to config keys, as shown in `docker-compose.example.yml`.
- `ConnectionStrings:DefaultConnection` is a standard ASP.NET Core connection string, not part of `DamebooruConfig`.

## Available Settings

### Storage

| Path | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Damebooru:Storage:DatabasePath` | `Damebooru__Storage__DatabasePath` | `data/damebooru.db` | SQLite database path |
| `Damebooru:Storage:ThumbnailPath` | `Damebooru__Storage__ThumbnailPath` | `data/thumbnails` | Thumbnail storage root |
| `Damebooru:Storage:TempPath` | `Damebooru__Storage__TempPath` | `data/temp` | Temporary processing files |

When startup detects pending EF Core migrations, Damebooru creates a pre-migration SQLite backup under a `backups/` directory next to the configured database file. For the default Docker mount, that means `./data/server/backups/` on the host.

### Scanner

| Path | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Damebooru:Scanner:BatchSize` | `Damebooru__Scanner__BatchSize` | `100` | Scan batch size |
| `Damebooru:Scanner:Parallelism` | `Damebooru__Scanner__Parallelism` | `2` | Parallelism for library scan work |
| `Damebooru:Scanner:EnableWatcher` | `Damebooru__Scanner__EnableWatcher` | `true` | Enables realtime filesystem watching |
| `Damebooru:Scanner:WatcherDebounceMs` | `Damebooru__Scanner__WatcherDebounceMs` | `2000` | Debounce window for watcher event bursts |
| `Damebooru:Scanner:WatcherReloadIntervalSeconds` | `Damebooru__Scanner__WatcherReloadIntervalSeconds` | `30` | How often library watcher registrations refresh |

### Processing

| Path | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Damebooru:Processing:RunScheduler` | `Damebooru__Processing__RunScheduler` | `true` | Enables the cron-style background scheduler |
| `Damebooru:Processing:MetadataParallelism` | `Damebooru__Processing__MetadataParallelism` | `2` | Metadata extraction parallelism |
| `Damebooru:Processing:SimilarityParallelism` | `Damebooru__Processing__SimilarityParallelism` | `2` | Similarity hash parallelism |
| `Damebooru:Processing:ThumbnailParallelism` | `Damebooru__Processing__ThumbnailParallelism` | `2` | Thumbnail generation parallelism |
| `Damebooru:Processing:JobProgressReportIntervalMs` | `Damebooru__Processing__JobProgressReportIntervalMs` | `1000` | Job progress update interval |

### Ingestion

| Path | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Damebooru:Ingestion:BatchSize` | `Damebooru__Ingestion__BatchSize` | `100` | Batch size for channel-based ingestion |
| `Damebooru:Ingestion:ChannelCapacity` | `Damebooru__Ingestion__ChannelCapacity` | `1000` | Capacity of the ingestion channel |

### Auth

| Path | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Damebooru:Auth:Enabled` | `Damebooru__Auth__Enabled` | `true` | Enables login protection |
| `Damebooru:Auth:Username` | `Damebooru__Auth__Username` | `admin` | Login username |
| `Damebooru:Auth:Password` | `Damebooru__Auth__Password` | `change-me` | Login password; prefer secrets in containers |

### Proxy

| Path | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Damebooru:Proxy:TrustForwardedHeaders` | `Damebooru__Proxy__TrustForwardedHeaders` | `false` | Enable only behind a trusted reverse proxy |

### Logging

| Path | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Damebooru:Logging:Db:Enabled` | `Damebooru__Logging__Db__Enabled` | `true` | Enables DB-backed log storage |
| `Damebooru:Logging:Db:MinimumLevel` | `Damebooru__Logging__Db__MinimumLevel` | `Warning` | Minimum DB log level |
| `Damebooru:Logging:Db:BatchSize` | `Damebooru__Logging__Db__BatchSize` | `100` | DB log write batch size |
| `Damebooru:Logging:Db:FlushIntervalMs` | `Damebooru__Logging__Db__FlushIntervalMs` | `1000` | DB log flush interval |
| `Damebooru:Logging:Db:ChannelCapacity` | `Damebooru__Logging__Db__ChannelCapacity` | `10000` | DB log channel capacity |
| `Damebooru:Logging:Db:RetentionDays` | `Damebooru__Logging__Db__RetentionDays` | `7` | Days to retain DB logs |
| `Damebooru:Logging:Db:MaxRows` | `Damebooru__Logging__Db__MaxRows` | `10000` | Maximum stored DB log rows |
| `Damebooru:Logging:Db:RetentionCheckIntervalMinutes` | `Damebooru__Logging__Db__RetentionCheckIntervalMinutes` | `15` | Retention cleanup interval |

### AI Tagging

| Path | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Damebooru:AiTagging:Enabled` | `Damebooru__AiTagging__Enabled` | `false` | Enables calls to the optional local AI tagging sidecar |
| `Damebooru:AiTagging:BaseUrl` | `Damebooru__AiTagging__BaseUrl` | `http://ai-tagging:8000` | AI tagging sidecar base URL |
| `Damebooru:AiTagging:TimeoutSeconds` | `Damebooru__AiTagging__TimeoutSeconds` | `300` | Request timeout |
| `Damebooru:AiTagging:UserAgent` | `Damebooru__AiTagging__UserAgent` | `Damebooru/1.0` | HTTP user agent |
| `Damebooru:AiTagging:MinConfidence` | `Damebooru__AiTagging__MinConfidence` | `0.01` | Internal low-confidence floor sent to the sidecar |
| `Damebooru:AiTagging:TopK` | `Damebooru__AiTagging__TopK` | `256` | Maximum tags per category returned by the sidecar |

AI tagging suggestion/apply thresholds are runtime settings stored in SQLite and exposed at `/api/settings/ai-tagging`.

#### AI Tagging Sidecar Container

The optional `ai-tagging` service is a separate FastAPI container. The ASP.NET server talks to it over the Docker Compose network using `Damebooru__AiTagging__BaseUrl`, usually `http://ai-tagging:8000`.

If your Docker UI does not support Compose profiles, remove this block from the `ai-tagging` service in your real compose file:

```yaml
profiles:
  - ai
```

CPU image example:

```yaml
ai-tagging:
  image: ghcr.io/yelovsk/damebooru-ai-tagging:latest
  restart: unless-stopped
  environment:
    AI_TAGGING_PROVIDER: cpu
  volumes:
    - ./data/ai-tagging/models:/models
```

OpenVINO image example for Intel GPU experiments:

```yaml
ai-tagging:
  image: ghcr.io/yelovsk/damebooru-ai-tagging:openvino
  restart: unless-stopped
  devices:
    - /dev/dri:/dev/dri
  environment:
    AI_TAGGING_PROVIDER: openvino
    AI_TAGGING_OPENVINO_DEVICE: GPU
    AI_TAGGING_OPENVINO_CACHE_DIR: /openvino-cache
  volumes:
    - ./data/ai-tagging/models:/models
    - ./data/ai-tagging/openvino-cache:/openvino-cache
```

Depending on host/NAS permissions, OpenVINO GPU may also require adding the container user to the host `render` or `video` group. Check the `ai-tagging` container logs on startup; it prints available, requested, and active ONNX Runtime providers.

| Variable | Default | Notes |
| --- | --- | --- |
| `AI_TAGGING_HOST` | `127.0.0.1` locally, `0.0.0.0` in Docker | Sidecar bind host |
| `AI_TAGGING_PORT` | `8000` | Sidecar bind port |
| `AI_TAGGING_MODEL_REPO` | `Camais03/camie-tagger-v2` | Hugging Face repo |
| `AI_TAGGING_MODEL_FILE` | `camie-tagger-v2.onnx` | ONNX model filename |
| `AI_TAGGING_METADATA_FILE` | `camie-tagger-v2-metadata.json` | Metadata filename |
| `AI_TAGGING_MODEL_DIR` | `./models` locally, `/models` in Docker | Runtime model cache |
| `AI_TAGGING_PROVIDER` | `cpu` | ONNX Runtime provider selector: `cpu`, `cuda`, `directml`, or `openvino` |
| `AI_TAGGING_OPENVINO_DEVICE` | `CPU` | OpenVINO target device, for example `CPU`, `GPU`, or `AUTO` |
| `AI_TAGGING_OPENVINO_CACHE_DIR` | empty | Optional OpenVINO compiled model cache directory, passed through OpenVINO `CACHE_DIR` |
| `AI_TAGGING_DEFAULT_THRESHOLD` | `0.492` | Sidecar default threshold; Damebooru normally sends the DB-backed suggestion threshold |
| `AI_TAGGING_MIN_CONFIDENCE` | `0.01` | Sidecar default confidence floor |
| `AI_TAGGING_TOP_K` | `256` | Sidecar default maximum tags per category |
| `AI_TAGGING_MAX_UPLOAD_MB` | `32` | Sidecar upload limit |

### External APIs

#### SauceNAO

| Path | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Damebooru:ExternalApis:SauceNao:BaseUrl` | `Damebooru__ExternalApis__SauceNao__BaseUrl` | `https://saucenao.com` | SauceNAO base URL |
| `Damebooru:ExternalApis:SauceNao:TimeoutSeconds` | `Damebooru__ExternalApis__SauceNao__TimeoutSeconds` | `30` | Request timeout |
| `Damebooru:ExternalApis:SauceNao:UserAgent` | `Damebooru__ExternalApis__SauceNao__UserAgent` | `Damebooru/1.0` | HTTP user agent |
| `Damebooru:ExternalApis:SauceNao:ApiKey` | `Damebooru__ExternalApis__SauceNao__ApiKey` | empty | API key |
| `Damebooru:ExternalApis:SauceNao:ResultsCount` | `Damebooru__ExternalApis__SauceNao__ResultsCount` | `10` | Result count |
| `Damebooru:ExternalApis:SauceNao:Database` | `Damebooru__ExternalApis__SauceNao__Database` | `999` | SauceNAO database selector |
| `Damebooru:ExternalApis:SauceNao:MinimumSimilarity` | `Damebooru__ExternalApis__SauceNao__MinimumSimilarity` | `60` | Minimum accepted similarity |

#### Danbooru

| Path | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Damebooru:ExternalApis:Danbooru:BaseUrl` | `Damebooru__ExternalApis__Danbooru__BaseUrl` | `https://danbooru.donmai.us` | Danbooru base URL |
| `Damebooru:ExternalApis:Danbooru:TimeoutSeconds` | `Damebooru__ExternalApis__Danbooru__TimeoutSeconds` | `30` | Request timeout |
| `Damebooru:ExternalApis:Danbooru:UserAgent` | `Damebooru__ExternalApis__Danbooru__UserAgent` | `Damebooru/1.0` | HTTP user agent |
| `Damebooru:ExternalApis:Danbooru:Username` | `Damebooru__ExternalApis__Danbooru__Username` | empty | Danbooru username |
| `Damebooru:ExternalApis:Danbooru:ApiKey` | `Damebooru__ExternalApis__Danbooru__ApiKey` | empty | Danbooru API key |

#### Gelbooru

| Path | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Damebooru:ExternalApis:Gelbooru:BaseUrl` | `Damebooru__ExternalApis__Gelbooru__BaseUrl` | `https://gelbooru.com` | Gelbooru base URL |
| `Damebooru:ExternalApis:Gelbooru:TimeoutSeconds` | `Damebooru__ExternalApis__Gelbooru__TimeoutSeconds` | `30` | Request timeout |
| `Damebooru:ExternalApis:Gelbooru:UserAgent` | `Damebooru__ExternalApis__Gelbooru__UserAgent` | `Damebooru/1.0` | HTTP user agent |
| `Damebooru:ExternalApis:Gelbooru:UserId` | `Damebooru__ExternalApis__Gelbooru__UserId` | empty | Gelbooru user id |
| `Damebooru:ExternalApis:Gelbooru:ApiKey` | `Damebooru__ExternalApis__Gelbooru__ApiKey` | empty | Gelbooru API key |

#### IQDB

| Path | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Damebooru:ExternalApis:Iqdb:BaseUrl` | `Damebooru__ExternalApis__Iqdb__BaseUrl` | `https://iqdb.org` | IQDB base URL |
| `Damebooru:ExternalApis:Iqdb:TimeoutSeconds` | `Damebooru__ExternalApis__Iqdb__TimeoutSeconds` | `30` | Request timeout |
| `Damebooru:ExternalApis:Iqdb:UserAgent` | `Damebooru__ExternalApis__Iqdb__UserAgent` | `Damebooru/1.0` | HTTP user agent |
| `Damebooru:ExternalApis:Iqdb:MinimumSimilarity` | `Damebooru__ExternalApis__Iqdb__MinimumSimilarity` | `75` | Minimum accepted similarity |
