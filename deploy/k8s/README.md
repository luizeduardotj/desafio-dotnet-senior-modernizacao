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
    ├── local/                    k3d na rede do compose; Secrets gerados com valores de teste
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

No overlay **local**, a fachada lê **um arquivo** de flags (`FonteDeFlagsPorConfiguracao`, ver [src/Nfse.Fachada/README.md](../../src/Nfse.Fachada/README.md)). Em produção ela lê o flagd (seção seguinte). O arquivo vem do ConfigMap `fachada-flags` (gerado de [base/fachada/flags.json](base/fachada/flags.json), M0: tudo em `legado`), montado como diretório em `/etc/nfse/flags`, com `DOTNET_USE_POLLING_FILE_WATCHER=true`:

- sem hash no nome (`disableNameSuffixHash`): mudar as flags **não** reinicia os pods; o kubelet troca o arquivo e a fachada recarrega;
- diretório e não `subPath`: `subPath` nunca recebe atualização;
- o tempo até todas as réplicas verem a mudança é o período de sync do kubelet mais o cache do ConfigMap (**dezenas de segundos a ~1–2 min**), mais até 4 s do polling. É exatamente por isso que a ADR-0008 diz que ConfigMap **não serve** como nível 1 do kill switch. Aqui ele é o caminho para **ligar** modos (por PR revisado), não para desligar com pressa.

### O que o overlay de produção acrescenta: flagd

`overlays/producao/flagd/` sobe um **flagd** (2 réplicas, PDB, RBAC só de leitura) que observa o CRD `FeatureFlag` `nfse-migracao` por watch na API do Kubernetes. O CR tem as flags no formato do flagd: `migracao.kill-switch`, `<Operacao>.kill-switch` e `<Operacao>.config`.

A fachada de produção consome o flagd (`Flags__Fonte=openfeature` no `fachada-config` do overlay): `FonteDeFlagsOpenFeature` com o provider flagd em **modo in-process**, no stream de sync do Service `flagd:8015`, com seletor `core.openfeature.dev/nfse/nfse-migracao`. A avaliação roda na própria fachada (sem RPC por requisição, cabe no orçamento de ≤ 10 ms) e cada mudança do CR chega pelo stream, em segundos. O mapeamento do CR para `FlagsDeMigracao` e o fail-safe estão no [README da fachada](../../src/Nfse.Fachada/README.md#openfeature--flagd).

- **flagd fora ou stream caído:** o provider emite erro e a fachada passa a mandar tudo ao legado, com `/health/ready` 503 (fail-safe da ADR-0008), até o sync voltar (o provider tenta de novo com backoff de até 60 s). Ver o risco de readiness abaixo.
- O ConfigMap `fachada-flags` continua montado também em produção, mas **sem efeito** enquanto a fonte for `openfeature`. Falta: medir o drill do nível 1 (< 10 s) e então tirar o ConfigMap do overlay.

### Mapeamento para os níveis do plano (seção 8)

| Nível | Hoje, nestes manifests | Comando |
| --- | --- | --- |
| **1** flag `kill-switch` | Produção: CR `FeatureFlag` `nfse-migracao`, pelo flagd (segundos). Local: ConfigMap `fachada-flags` (lento, ver acima) | produção: `kubectl -n nfse patch featureflag nfse-migracao ...` (comando no topo de [`featureflag.yaml`](overlays/producao/flagd/featureflag.yaml)); local: `kubectl -n nfse edit configmap fachada-flags` (`"KillSwitch": true`) |
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

## Como aplicar no k3d

Pré-requisitos: k3d ≥ 5.6 (Kubernetes ≥ 1.30, por `preStop.sleep` e `matchLabelKeys`) e o `docker compose up` da raiz de pé. O cluster entra na **rede do compose**: o legado, o Oracle e o otel-lgtm ficam no compose, fora do cluster, como em produção, e o k3d injeta os nomes dessa rede no CoreDNS. Por isso o overlay local aponta `legado` → `webservices`, `oracle` → `oracle` e o OTLP para `otel-lgtm:4317`. `host.docker.internal` não serve: o legado não publica porta no host e o Oracle só escuta em `127.0.0.1`.

Os comandos usam um kubeconfig próprio, para não mexer no contexto padrão:

```bash
export KUBECONFIG=$PWD/.k3d-nfse.kubeconfig
```

```bash
k3d cluster create nfse --network desafio-dotnet-senior-modernizacao_default --kubeconfig-update-default=false --kubeconfig-switch-context=false --wait
```

```bash
k3d kubeconfig get nfse > $KUBECONFIG
```

```bash
docker tag nfse-fachada:0.1.0 nfse-fachada:dev && docker tag nfse-novo:0.1.0 nfse-novo:dev && k3d image import nfse-fachada:dev nfse-novo:dev -c nfse
```

```bash
kubectl apply -k deploy/k8s/overlays/local && kubectl -n nfse rollout status deploy/fachada
```

```bash
kubectl -n nfse port-forward svc/fachada 18080:8080
```

O SOAP responde em `http://127.0.0.1:18080/NfseWS.asmx`. Para ver o YAML gerado sem cluster: `kubectl kustomize deploy/k8s/overlays/local`.

- O k3d traz o Traefik, e os Ingress pedem `ingressClassName: nginx`. Para exercitar o Ingress, crie o cluster com `--k3s-arg "--disable=traefik@server:0"` e instale o ingress-nginx. Sem isso, use o port-forward.
- O namespace do pod precisa cumprir o Pod Security `restricted` (`nfse` é `enforce`), e a NetworkPolicy da fachada só aceita tráfego do `ingress-nginx` (8080) e do `nfse-operacao` (8081). Um gerador de carga dentro do cluster precisa de securityContext completo e de uma política que libere o namespace dele.
- O Secret local tem valores de teste (`token-admin-local-dev`; senhas `*_dev_123`, que precisam ser as passadas a `db/moderno/01_usuarios.sql`).

## Riscos conhecidos

- **Readiness e fail-safe das flags.** `/health/ready` responde 503 quando as flags não carregam. Se um `flags.json` inválido chegar a **todas** as réplicas, o Service `fachada` fica sem endpoints e o SOAP inteiro cai, embora o fail-safe da fachada mandasse tudo ao legado. Com o flagd em produção o risco cresce: **flagd fora** também tira as flags de todas as réplicas de uma vez. O readiness por flags é útil na subida do pod, não depois. Proposta para a fachada: falhar o `ready` só até a primeira carga válida e, depois disso, expor a falha por métrica e alerta. Até lá, validar o JSON no CI antes de aplicar.
- **Desligamento corta chamadas longas ao legado.** O grace period é 30 s (o `ShutdownTimeout` padrão do host .NET); uma chamada ao legado mais longa que ~25 s é cortada quando o pod sai. Leituras (5 s) e cancelamento (10 s) no novo cabem.
- O NetworkPolicy do `nfse-novo` libera o `ingress-nginx` na porta inteira; quem restringe a `/api/v1` é o Ingress, não a política (NetworkPolicy não vê caminho HTTP).

## Validação

**Cluster k3d, 2026-10-19 [M]** (k3d 5.9, k3s 1.36, Docker Desktop com 8 CPUs; passo a passo acima):

| O que | Resultado |
| --- | --- |
| `kubectl apply -k overlays/local` | todos os objetos criados; Pod Security `restricted` aceitou fachada e nfse-novo; 2 + 2 réplicas prontas |
| SOAP pela fachada, modo `legado` | `ConsultarNfse` 200 pelo legado do compose |
| Troca para `novo` pelo ConfigMap | ~36 s até o kubelet trocar o arquivo no pod, mais até 4 s do polling; depois disso, 6 de 6 respostas pelo novo (`nfse_requisicoes_total{destino="novo"}`), byte a byte iguais às do legado. Confirma a seção [Flags e kill switch](#flags-e-kill-switch): ConfigMap liga modos, não desliga com pressa |
| `rollout restart` da fachada e do novo sob carga (k6 dentro do cluster, 10 + 1 req/s) | p99 de 119 ms (ConsultarNfse) e 874 ms (ServicoTomado); **2 de 662** `connection refused` |
| O mesmo `rollout restart` da fachada sem as NetworkPolicies | **0 de 661** falhas |

As duas falhas vêm do controlador de NetworkPolicy do k3s (kube-router): ele libera o IP do pod novo depois que o pod já está pronto e recebendo tráfego, e o pacote é rejeitado. Repetiu nas duas rodadas com política e não apareceu sem ela. Não mudei os manifests por isso, porque o comportamento é do CNI: em produção, repetir o teste com o CNI do cluster (Cilium ou Calico) antes de confiar em zero erro no rollout. Se repetir lá, a correção é um `initialDelaySeconds` no readiness da fachada maior que o tempo de programação da política.

`kubectl kustomize` dos dois overlays roda sem erro e passa no `kubeconform` v0.8.0 (`-strict`), medido localmente:

| Overlay | Recursos | Válidos | Inválidos | Ignorados |
| --- | --- | --- | --- | --- |
| `local` | 22 | 22 | 0 | 0 |
| `producao` | 34 | 34 | 0 | 0 |

```bash
CAT='https://raw.githubusercontent.com/datreeio/CRDs-catalog/main/{{.Group}}/{{.ResourceKind}}_{{.ResourceAPIVersion}}.json'
kubectl kustomize deploy/k8s/overlays/local | kubeconform -strict -summary -schema-location default -schema-location "$CAT" -
kubectl kustomize deploy/k8s/overlays/producao | kubeconform -strict -summary -schema-location default -schema-location "$CAT" -
```

Os CRDs de produção (Rollout e AnalysisTemplate do Argo Rollouts, ExternalSecret, FeatureFlag do OpenFeature operator) são validados de verdade pelo [datreeio/CRDs-catalog](https://github.com/datreeio/CRDs-catalog): todos existem lá, então **não se usa `-ignore-missing-schemas`** e o resumo mostra 0 ignorados. Se um CRD novo não tiver schema no catálogo, o job falha em vez de pular em silêncio. Para confirmar que a checagem pega erro, renomear `maxSurge` em um Rollout faz o kubeconform reprovar o recurso (`additional properties 'maxSurgeX' not allowed`).

O job `manifests` do [CI](../../.github/workflows/ci.yml) repete isso a cada push e PR, com kubectl (kustomize embutido) e kubeconform em versão e SHA-256 fixos. Limite: valida o formato, não o comportamento; o overlay de produção (Argo Rollouts, flagd, External Secrets) não foi aplicado em cluster.
