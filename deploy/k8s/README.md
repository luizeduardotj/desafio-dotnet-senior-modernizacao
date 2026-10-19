# Kubernetes (Kustomize)

Manifests da fachada (`nfse-fachada`) e do serviço novo (`nfse-novo`). O legado e o Oracle **continuam fora do cluster**, como em produção hoje ([ADR-0001](../../docs/adr/0001-estrategia-strangler-fig-e-ordem-de-extracao.md)). Decisões de topologia e flags em [ADR-0008](../../docs/adr/0008-fachada-yarp-roteamento-e-flags.md), telemetria e segredos em [ADR-0012](../../docs/adr/0012-observabilidade-seguranca-e-lgpd.md), níveis do kill switch no [plano de migração, seção 8](../../docs/plano-de-migracao.md#8-níveis-do-kill-switch).

```
deploy/k8s/
├── base/
│   ├── namespace.yaml            namespace nfse, Pod Security "restricted"
│   ├── ingress.yaml              nfse-soap (→ fachada) e nfse-rest (/api/v1 → nfse-novo)
│   ├── legado/                   Services ExternalName "legado" e "oracle" (fora do cluster)
│   ├── fachada/                  Deployment, Service 8080, Service admin 8081, PDB, NetworkPolicy, flags.json
│   └── nfse-novo/                Deployment, Service, HPA, PDB, NetworkPolicy
├── components/
│   └── break-glass/              nível 3: nfse-soap aponta direto para o legado
└── overlays/
    ├── local/                    kind/k3d; Secrets gerados com valores de teste
    └── producao/                 Argo Rollouts + análise, ExternalSecrets, flagd + FeatureFlag
```

## Topologia

| Tráfego | Caminho no cluster |
| --- | --- |
| ERP → SOAP (north-south) | Ingress `nfse-soap` → Service `fachada:8080` → `legado` (ExternalName) ou `nfse-novo:8080` |
| App/portal → REST (north-south) | Ingress `nfse-rest` (`/api/v1`) → `nfse-novo:8080`, sem passar pela fachada |
| Operação → kill switch nível 2 | namespace `nfse-operacao` → Service headless `fachada-admin:8081` (um IP por réplica) |
| Fachada → sombra do cancelamento (east-west) | `nfse-novo:8081` (`POST /interno/sombra/cancelamento`), porta só da fachada ([ADR-0009](../../docs/adr/0009-sombra-escrita-unica-e-idempotencia.md), seção 3) |
| `nfse-novo` → Oracle (east-west) | `oracle:1521` (ExternalName), usuários `NFSE_MODERNO_APP` e `NFSE_MODERNO_RO` |

O Ingress SOAP tem `proxy-body-size: 0` (o legado não limita o corpo e a fachada também não), timeout de 120 s (acima do `Legado__Timeout` de 1 min 40 s, para quem corta ser a fachada) e `proxy-next-upstream: off` (um `CancelarNfse` não pode ser reenviado pelo ingress a outra réplica).

## Fachada

| Item | Valor | Por quê |
| --- | --- | --- |
| Réplicas | 2 na base, 3 em produção | ADR-0008: ≥ 2, sem estado |
| Rollout | `RollingUpdate`, `maxUnavailable: 0`, `maxSurge: 1` | nunca abaixo da capacidade |
| Probes | `startupProbe` e `livenessProbe` em `/health/live`, `readinessProbe` em `/health/ready`, todas na **8080** | a 8081 só tem `/admin` (`PortaDoEndpoint`) |
| Recursos **[H]** | request 250m / 256Mi, limite de memória 512Mi, **sem limite de CPU** | throttling do CFS num proxy estoura o orçamento de ≤ 5 ms no p99; calibrar no teste de carga legado direto × via fachada |
| Espalhamento | `topologySpreadConstraints` por nó (rígido) e zona (preferencial), `matchLabelKeys: pod-template-hash` | uma réplica por nó mesmo durante o rollout |
| PDB | `maxUnavailable: 1` | drenagem sem derrubar a fachada |
| Segurança | não root (UID 1654), rootfs só leitura, `drop: ALL`, seccomp, sem token de ServiceAccount | `/tmp` é `emptyDir` |
| Admin | Service headless `fachada-admin`, só a porta 8081, `publishNotReadyAddresses` | o comando vale por réplica; uma réplica fora do Service de dados ainda recebe o kill switch |
| NetworkPolicy | 8080 só do `ingress-nginx`; 8081 só do namespace `nfse-operacao` | |
| Segredo | `Admin__Token` do Secret `fachada-admin` | local: `secretGenerator`; produção: `ExternalSecret` |
| Telemetria | `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES` | lidas pelo SDK OpenTelemetry quando ele for ligado (TODO da fachada) |

`TZ` não é definido de propósito: a ADR-0012 fixa `America/Sao_Paulo` no código, e o fuso do pod nunca é usado.

## Serviço novo

Mesmo padrão de probes, segurança e rollout. Diferenças:

- **HPA por CPU** (70% do request, 2 a 6 réplicas; 3 a 6 em produção) **[H]**. O teto vem do Oracle, não do custo: `maxReplicas × Max Pool Size` precisa caber no orçamento de sessões combinado com o DBA (6 × 20 = 120). O Deployment não declara `replicas`, para o `apply` não brigar com o HPA.
- **Secret `nfse-novo-oracle`** com duas connection strings, com os nomes que o `Nfse.Host` lê: `ConnectionStrings__Oracle` (usuário `NFSE_MODERNO_APP`, que escreve só pelo `PKG_CANCELAMENTO_API`) e `ConnectionStrings__OracleSomenteLeitura` (usuário `NFSE_MODERNO_RO`, da sombra do cancelamento).
- **Readiness** em `/health/ready` (o check do Oracle ainda é um TODO no Host); a **liveness não checa o banco** (Oracle lento reiniciando todos os pods só piora o incidente).
- NetworkPolicy: 8080 só da fachada (SOAP) e do `ingress-nginx` (REST); 8081 (interna, sombra do cancelamento) só da fachada.
- A porta `interno` 8081 está no Deployment e no Service. O `Nfse.Host` escuta nas duas portas (`Porta`, padrão 8080, e `Interno:Porta`, padrão 8081), e o endpoint `/interno/sombra/cancelamento` só responde na 8081.

## Flags e kill switch

### O que existe hoje

A fachada lê **um arquivo** de flags (`FonteDeFlagsPorConfiguracao`, ver [src/Nfse.Fachada/README.md](../../src/Nfse.Fachada/README.md)). Nos dois overlays esse arquivo vem do ConfigMap `fachada-flags` (gerado de [base/fachada/flags.json](base/fachada/flags.json), M0: tudo em `legado`), montado como diretório em `/etc/nfse/flags`, com `DOTNET_USE_POLLING_FILE_WATCHER=true`:

- sem hash no nome (`disableNameSuffixHash`): mudar as flags **não** reinicia os pods; o kubelet troca o arquivo e a fachada recarrega;
- diretório e não `subPath`: `subPath` nunca recebe atualização;
- o tempo até todas as réplicas verem a mudança é o período de sync do kubelet mais o cache do ConfigMap (**dezenas de segundos a ~1–2 min**), mais até 4 s do polling. É exatamente por isso que a ADR-0008 diz que ConfigMap **não serve** como nível 1 do kill switch. Aqui ele é o caminho para **ligar** modos (por PR revisado), não para desligar com pressa.

### O que o overlay de produção acrescenta, e o que ainda não está ligado

`overlays/producao/flagd/` sobe um **flagd** (2 réplicas, PDB, RBAC só de leitura) que observa o CRD `FeatureFlag` `nfse-migracao` por watch na API do Kubernetes. O CR já tem as flags no formato do flagd: `migracao.kill-switch`, `<Operacao>.kill-switch` e `<Operacao>.config`.

**A fachada ainda não consome o flagd.** Nada nos manifests finge o contrário: não há variável `FLAGD_*` na fachada, e o nível 1 em produção, até o próximo passo, continua sendo o ConfigMap (lento). Enquanto isso, **o caminho rápido real é o nível 2**.

Próximo passo (TODO da fachada, ADR-0008):

1. `IFonteDeFlags` sobre OpenFeature com o provider flagd em **modo in-process** apontando para `flagd:8015` (sync): a avaliação roda na própria fachada (sem RPC por requisição, cabe no orçamento de ≤ 10 ms) e o último retrato continua valendo se o flagd cair.
2. Mapear o CR para o mesmo `FlagsDeMigracao`, com o fail-safe para `null` (tudo ao legado) se o provider não tiver retrato.
3. Adicionar `FLAGD_HOST`/`FLAGD_PORT`/`FLAGD_RESOLVER=in-process` ao `fachada-config` de produção e medir o drill (< 10 s).
4. Só então tirar o ConfigMap `fachada-flags` do overlay de produção.

### Mapeamento para os níveis do plano (seção 8)

| Nível | Hoje, nestes manifests | Comando |
| --- | --- | --- |
| **1** flag `kill-switch` | ConfigMap `fachada-flags` (lento, ver acima). Em produção o CR do flagd está pronto, mas sem consumidor | `kubectl -n nfse edit configmap fachada-flags` (`"KillSwitch": true`), ou alterar `base/fachada/flags.json` e aplicar |
| **2** `POST /admin/kill-switch` por réplica | Service headless `fachada-admin`, liberado só para `nfse-operacao` | script abaixo |
| **3** break-glass | componente `components/break-glass` troca o backend do Ingress `nfse-soap` para o Service `legado` | ver abaixo |
| **4** rollback de versão | `kubectl rollout undo` no overlay local; em produção, `kubectl argo rollouts abort/undo` (e a análise aborta sozinha) | `kubectl argo rollouts -n nfse undo fachada` |

Nível 2, de um pod no namespace de operação (o token vem do Secret, nunca da linha de comando versionada):

```bash
kubectl -n nfse-operacao run kill-switch --rm -it --restart=Never --image=curlimages/curl:8.10.1 --env="TOKEN=$(kubectl -n nfse get secret fachada-admin -o jsonpath='{.data.token}' | base64 -d)" --command -- sh -c 'for ip in $(nslookup fachada-admin.nfse.svc.cluster.local | awk "/^Address: /{print \$2}"); do curl -fsS -X POST -H "X-Admin-Token: $TOKEN" "http://$ip:8081/admin/kill-switch"; echo " $ip"; done'
```

O estado do nível 2 fica em memória: uma réplica nova (rollout, HPA, restart) **nasce sem ele**. Por isso o nível 2 é ponte até o nível 1 ser aplicado, não estado final.

Nível 3 (break-glass). Com GitOps, o caminho é o PR que descomenta `components: [../../components/break-glass]` em `overlays/producao/kustomization.yaml`. Em emergência, direto no cluster (pausando o sync do GitOps, senão ele desfaz):

```bash
kubectl -n nfse patch ingress nfse-soap --type=json -p='[{"op":"replace","path":"/spec/rules/0/http/paths/0/backend/service/name","value":"legado"}]'
```

O Service `legado` é `ExternalName`; o ingress-nginx aceita esse backend. Depois do M5 de uma operação, o break-glass não a cobre mais (ADR-0008).

## Rollout de versões (Argo Rollouts, só em produção)

Isso é o rollout das **versões** da fachada e do serviço novo. **Não é o strangler**: quem decide legado × novo por operação, prestador e percentual são as flags da fachada. A ADR-0008 rejeita o canário do Kubernetes como mecanismo do strangler (ele divide réplicas de um mesmo serviço, não escolhe entre dois sistemas por operação).

- Os `Rollout` usam `workloadRef` para o Deployment da base: o template (imagem, probes, recursos) fica num lugar só, e o Deployment é escalado a zero no overlay. Num cluster que já roda os Deployments, a migração é aplicar primeiro os `Rollout` com `scaleDown: progressively` e só depois o patch de `replicas: 0`.
- Sem `trafficRouting`: o peso é a proporção de réplicas. Fachada: 34% → 15 min → 67% → 15 min → 100%. Serviço novo: 25% → 50% → **pausa manual** antes de 100% (o cancelamento escreve; alguém olha o dual-run do plano §6.2 e promove com `kubectl argo rollouts promote`).
- A análise roda em segundo plano desde o primeiro degrau e compara **canário × estável** na mesma janela, com os limites do plano (§6.1, §9): taxa de 5xx até +0,1 p.p. e p99 até 1,2 × o estável. A comparação é relativa porque os `soap:Fault` intencionais (C20) também são 500. Sem tráfego, o resultado fica inconclusivo e o rollout pausa: não se promove às cegas.
- Premissas da análise **[H]**: os serviços exportam `http.server.request.duration` por OTLP (TODO de OpenTelemetry na fachada e no serviço novo), o OTel Collector anexa o label de pod `rollouts-pod-template-hash` (`k8sattributes`) e `service.name` vira `job` no Prometheus. Até isso existir, a análise fica inconclusiva e todo rollout pausa para decisão humana.

## Como aplicar no kind

Pré-requisitos: kind ou k3d com Kubernetes ≥ 1.30 (`preStop.sleep`, `matchLabelKeys`), ingress-nginx e um CNI que aplique NetworkPolicy (sem isso as políticas são aceitas e ignoradas; no kind, confira a versão do kindnet ou use Calico). O legado e o Oracle sobem pelo `docker compose up` da raiz.

```bash
docker compose up -d
kind create cluster --name nfse
kubectl apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/controller-v1.11.3/deploy/static/provider/kind/deploy.yaml
kind load docker-image nfse-fachada:dev nfse-novo:dev --name nfse
kubectl create namespace nfse-operacao
kubectl apply -k deploy/k8s/overlays/local
kubectl -n nfse rollout status deploy/fachada
```

Para ver o resultado gerado sem cluster:

```bash
kubectl kustomize deploy/k8s/overlays/local
```

- O overlay local aponta `legado` e `oracle` para `host.docker.internal`. As portas do compose estão publicadas em `127.0.0.1`; no Docker Desktop isso alcança o host. Se não alcançar (Linux, Colima), conecte o nó do kind à rede do compose (`docker network connect <projeto>_default nfse-control-plane`) e troque o `externalName` para `webservices`/`oracle`.
- O host do Ingress local é `nfse.localhost`.
- O Secret local tem valores de teste (`token-admin-local-dev`; senhas `*_dev_123`, que precisam ser as passadas a `db/moderno/01_usuarios.sql`).

## Riscos conhecidos

- **Readiness e fail-safe das flags.** `/health/ready` responde 503 quando as flags não carregam. Se um `flags.json` inválido chegar a **todas** as réplicas, o Service `fachada` fica sem endpoints e o SOAP inteiro cai, embora o fail-safe da fachada mandasse tudo ao legado. O readiness por flags é útil na subida do pod, não depois. Proposta para a fachada: falhar o `ready` só até a primeira carga válida e, depois disso, expor a falha por métrica e alerta. Até lá, validar o JSON no CI antes de aplicar.
- **Desligamento corta chamadas longas ao legado.** O grace period é 30 s (o `ShutdownTimeout` padrão do host .NET); uma chamada ao legado mais longa que ~25 s é cortada quando o pod sai. Leituras (5 s) e cancelamento (10 s) no novo cabem.
- O NetworkPolicy do `nfse-novo` libera o `ingress-nginx` na porta inteira; quem restringe a `/api/v1` é o Ingress, não a política (NetworkPolicy não vê caminho HTTP).

## Validação

Não havia `kubectl`, `kustomize` nem `kubeconform` na máquina em que estes manifests foram escritos, e nenhum foi instalado. A validação feita foi estrutural (todo arquivo YAML é válido, toda referência de `resources`/`patches`/`files`/`components` existe, todo objeto tem `apiVersion`, `kind` e `metadata.name`). **`kubectl kustomize` dos dois overlays não foi executado.** Para fechar:

```bash
kubectl kustomize deploy/k8s/overlays/local | kubeconform -strict -summary -
kubectl kustomize deploy/k8s/overlays/producao | kubeconform -strict -summary -ignore-missing-schemas -
```

Em produção, `-ignore-missing-schemas` cobre os CRDs (Rollout, AnalysisTemplate, ExternalSecret, FeatureFlag), que não estão no catálogo padrão do kubeconform.
