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
validacao/
└── producao-k3d/                 só para testar overlays/producao num k3d: compose no lugar da plataforma, cofre fake
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
| Espalhamento | `topologySpreadConstraints` por nó (rígido) e zona (preferencial), `matchLabelKeys: pod-template-hash`, `nodeTaintsPolicy: Honor` | uma réplica por nó mesmo durante o rollout; nó em drain fora da conta (ver [Calico e vários nós](#calico-e-vários-nós)) |
| PDB | `maxUnavailable: 1` | drenagem sem derrubar a fachada |
| Segurança | não root (UID 1654), rootfs só leitura, `drop: ALL`, seccomp, sem token de ServiceAccount | `/tmp` é `emptyDir` |
| Admin | Service headless `fachada-admin`, só a porta 8081, `publishNotReadyAddresses` | o comando vale por réplica; uma réplica fora do Service de dados ainda recebe o kill switch |
| NetworkPolicy | 8080 só do `ingress-nginx`; 8081 só do namespace `nfse-operacao` | |
| Segredo | `Admin__Token` do Secret `fachada-admin` | local: `secretGenerator`; produção: `ExternalSecret` |
| Telemetria | `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES` | lidas pelo SDK OpenTelemetry da fachada |

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

A fachada de produção consome o flagd (`Flags__Fonte=openfeature` no `fachada-config` do overlay): `FonteDeFlagsOpenFeature` com o provider flagd em **modo in-process**, no stream de sync do Service `flagd:8015`, com seletor `nfse/nfse-migracao` (o flagd registra a fonte Kubernetes como `<namespace>/<nome>`, sem o prefixo `core.openfeature.dev/` do `--uri`). A avaliação roda na própria fachada (sem RPC por requisição, cabe no orçamento de ≤ 10 ms) e cada mudança do CR chega pelo stream, em segundos. O mapeamento do CR para `FlagsDeMigracao` e o fail-safe estão no [README da fachada](../../src/Nfse.Fachada/README.md#openfeature--flagd).

- **flagd fora ou stream caído:** o provider emite erro e a fachada passa a mandar tudo ao legado, com `/health/ready` 503 (fail-safe da ADR-0008), até o sync voltar (o provider tenta de novo com backoff de até 60 s). Ver o risco de readiness abaixo.
- Em produção o ConfigMap `fachada-flags` **não existe**: [`fachada-sem-arquivo-de-flags.yaml`](overlays/producao/fachada-sem-arquivo-de-flags.yaml) tira o ConfigMap, o volume e a variável `Fachada__ArquivoDeFlags`. Assim um `flags.json` esquecido não vira fonte por engano se alguém trocar `Flags__Fonte`. O drill do nível 1 foi medido no cluster pelo CR (0,12–0,24 s, ver [Validação](#validação)).

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
- A análise roda em segundo plano desde o primeiro degrau e compara **canário × estável** na mesma janela, com os limites do plano (§6.1, §9): taxa de 5xx até +0,1 p.p. e p99 até 1,2 × o estável. A comparação é relativa porque os `soap:Fault` intencionais (C20) também são 500. Sem tráfego, o resultado fica inconclusivo e o rollout pausa: não se promove às cegas. A primeira medição espera 7 min (`initialDelay`): 2 min de aquecimento do pod novo mais a janela de 5 min. Sem isso, o p99 de um pod frio reprovou uma versão idêntica à estável (ver [Validação](#validação)).
- O peso por proporção de réplicas só vale se o tráfego chegar às réplicas novas. A fachada recicla as conexões com o legado e o novo a cada 1 min (`PooledConnectionLifetime`); sem isso, sob carga constante, o canário do `nfse-novo` e o scale-out do HPA não recebiam nada da fachada (medido).
- Premissas da análise, conferidas no Prometheus do otel-lgtm com o overlay aplicado em k3d **[M]**: os serviços exportam `http.server.request.duration` por OTLP; o `job` é `nfse/<service.name>` (o `OTEL_RESOURCE_ATTRIBUTES` dos Deployments põe `service.namespace=nfse`); o hash da revisão sai do label `k8s_pod_name` (`<rollout>-<hash>-<sufixo>`), que o Prometheus promove de `k8s.pod.name`, sem depender do `k8sattributes` no Collector; as probes ficam fora da conta. Num Prometheus da plataforma que não promova `k8s.pod.name`, a análise fica inconclusiva e todo rollout pausa para decisão humana.

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

As duas falhas vêm do controlador de NetworkPolicy do k3s (kube-router): ele libera o IP do pod novo depois que o pod já está pronto e recebendo tráfego, e o pacote é rejeitado. Repetiu nas duas rodadas com política e não apareceu sem ela. Não mudei os manifests por isso, porque o comportamento é do CNI: em produção, repetir o teste com o CNI do cluster (Cilium ou Calico) antes de confiar em zero erro no rollout. Se repetir lá, a correção é um `initialDelaySeconds` no readiness da fachada maior que o tempo de programação da política. Com Calico não repetiu: 0 falhas em 4 restarts (ver [Calico e vários nós](#calico-e-vários-nós)).

`kubectl kustomize` dos dois overlays roda sem erro e passa no `kubeconform` v0.8.0 (`-strict`), medido localmente:

| Overlay | Recursos | Válidos | Inválidos | Ignorados |
| --- | --- | --- | --- | --- |
| `local` | 22 | 22 | 0 | 0 |
| `producao` | 33 | 33 | 0 | 0 |

```bash
CAT='https://raw.githubusercontent.com/datreeio/CRDs-catalog/main/{{.Group}}/{{.ResourceKind}}_{{.ResourceAPIVersion}}.json'
kubectl kustomize deploy/k8s/overlays/local | kubeconform -strict -summary -schema-location default -schema-location "$CAT" -
kubectl kustomize deploy/k8s/overlays/producao | kubeconform -strict -summary -schema-location default -schema-location "$CAT" -
```

Os CRDs de produção (Rollout e AnalysisTemplate do Argo Rollouts, ExternalSecret, FeatureFlag do OpenFeature operator) são validados de verdade pelo [datreeio/CRDs-catalog](https://github.com/datreeio/CRDs-catalog): todos existem lá, então **não se usa `-ignore-missing-schemas`** e o resumo mostra 0 ignorados. Se um CRD novo não tiver schema no catálogo, o job falha em vez de pular em silêncio. Para confirmar que a checagem pega erro, renomear `maxSurge` em um Rollout faz o kubeconform reprovar o recurso (`additional properties 'maxSurgeX' not allowed`).

O job `manifests` do [CI](../../.github/workflows/ci.yml) repete isso a cada push e PR, com kubectl (kustomize embutido) e kubeconform em versão e SHA-256 fixos. Limite: valida o formato, não o comportamento. O comportamento do overlay de produção foi verificado em cluster; ver a seção seguinte.

### Overlay de produção em k3d

**Cluster k3d `nfse-prod`, 2026-10-19 [M]** (k3d 5.9, k3s 1.36, sem Traefik; Docker Desktop com 8 CPUs e 12 GB). Instalado com versões fixadas: Argo Rollouts (chart 2.43.8, v1.10.1), External Secrets Operator (chart 2.13.0), OpenFeature Operator (v0.9.3, com cert-manager v1.21.2), ingress-nginx (chart 4.15.1). Os manifests são os de `overlays/producao`; [`validacao/producao-k3d`](validacao/producao-k3d/kustomization.yaml) troca só o que é da plataforma: endereços do legado, do Oracle e da observabilidade (para o compose) e o cofre, um `ClusterSecretStore` `cofre-nfse` com o provider **fake** do External Secrets e valores de teste ([`plataforma.yaml`](validacao/producao-k3d/plataforma.yaml)). A análise consulta o Prometheus do otel-lgtm do compose. Tráfego: k6 na rede do compose, `ConsultarNfse` a 4 req/s pelo ingress-nginx, com `ConsultarNfse` em `novo` 100% no CR (só no cluster).

```bash
k3d cluster create nfse-prod --network desafio-dotnet-senior-modernizacao_default --k3s-arg "--disable=traefik@server:0" --kubeconfig-update-default=false --kubeconfig-switch-context=false --wait
```

```bash
kubectl apply -f deploy/k8s/validacao/producao-k3d/plataforma.yaml && kubectl apply -k deploy/k8s/validacao/producao-k3d
```

As imagens precisam da tag que o overlay espera (`registry.substituir.invalid/nfse/nfse-*:0.1.0`). Com o Docker Desktop no image store do containerd, `k3d image import` termina com sucesso mas a imagem não aparece no `crictl images`; o que funcionou foi `docker save <imagem> | docker exec -i k3d-nfse-prod-server-0 ctr -n k8s.io images import -`.

| Cenário | Resultado |
| --- | --- |
| `apply` do zero (com as correções abaixo) | 34 objetos na época (33 depois que o ConfigMap de flags saiu de produção); ExternalSecrets `SecretSynced` pelo cofre fake; Pod Security `restricted` aceitou fachada, nfse-novo e flagd; os dois `Rollout` `Healthy` em 5 s. O `nfse-novo` nasce com 1 réplica (o Rollout não declara `replicas`) e o HPA leva a 3 |
| Kill switch nível 1 (`patch` do `defaultVariant` de `migracao.kill-switch` no CR), 5 trocas sob 30 req/s | todas as 3 réplicas no novo estado em **0,12–0,24 s** depois do patch; a última requisição no estado antigo saiu em ≤ 0,07 s; 0 erros. Referência no compose: 0,4 s |
| Canário da fachada 0.1.0 → 0.1.1 | canário com 1/3 do tráfego (34%); análise aprovou duas vezes (5xx: diferença 0; p99: 0,94× e 1,00×); promovido por 34% → 67% → 100% |
| Canário do `nfse-novo` 0.1.0 → 0.1.1 (mesmo binário) | 25% (1 canário + 3 estáveis) → 50% (2 + 3) → pausa manual → 100%; análise aprovou (5xx: diferença 0; p99: 1,03× e 1,09×) |
| Rollback automático pela análise (1ª tentativa, sem o `initialDelay`) | p99 do canário 2,40× e depois 1,27× o estável → `failureLimit` estourado e o Argo abortou 10 s depois da 2ª medição; canário escalado a zero, 3 estáveis atendendo |
| `abort` manual do canário 0.1.2 com tráfego (10 req/s; o canário já tinha atendido ≥ 126 requisições) | canário fora em **6,3 s** (inclui os 5 s do `preStop`); devolver o spec à versão estável deixou o Rollout `Healthy` em 0,1 s, sem pods novos; **0 de 1.213** falhas |
| Tráfego durante drills, canários e rollbacks | k6: **0 de 8.762** falhas, p99 17 ms |

Erros reais achados e corrigidos (um commit `fix(...)` cada):

- `ExternalSecret` em `external-secrets.io/v1beta1`: o External Secrets atual só serve `v1`, e o `apply` falhava.
- Seletor do flagd: o flagd v0.11.1 registra a fonte `core.openfeature.dev/nfse/nfse-migracao` como `nfse/nfse-migracao`. Com o seletor antigo, o stream de sync vinha vazio, toda flag dava `FlagNotFound` e as 3 réplicas da fachada ficavam fora do Service (readiness 503).
- Análise: o `job` no Prometheus é `nfse/<service.name>`; `rollouts_pod_template_hash` não existe (o hash sai de `k8s_pod_name`); sem 5xx a taxa ficava vazia (inconclusiva) no caso bom; as probes contavam como erro; e a janela começando no pod frio reprovava uma versão idêntica (`initialDelay: 7m`).
- `matchLabelKeys: pod-template-hash` não tem efeito sob o Argo Rollouts (os pods só têm `rollouts-pod-template-hash`).
- Fachada sem `PooledConnectionLifetime`: com conexões keep-alive eternas, o canário do `nfse-novo` e uma réplica estável ficaram 10 min sem nenhuma requisição. Corrigido no código (1 min).

Limites desta validação: um nó só (o espalhamento por nó e zona não é exercitado); CNI do k3s (kube-router), não o de produção (os dois repetidos em [Calico e vários nós](#calico-e-vários-nós)); a análise só viu `ConsultarNfse` a poucas req/s, e com tráfego real o p99 por janela de 5 min é bem mais estável que aqui; o cofre é fake. Durante a primeira metade do teste, a VM do Docker ficou sem memória (swap cheio) e a API do k3s caiu várias vezes; a 1ª tentativa de rollback automático é dessa fase. Os números da tabela, menos esse, são da segunda metade, com 12 GB e sem pressão.

### Calico e vários nós

**Cluster k3d `nfse-calico`, 2026-10-20 [M]**: 1 server + 2 agents (3 nós, todos agendáveis), sem flannel e sem o controlador de NetworkPolicy do k3s; **Calico v3.32.2** (manifest `calico.yaml` com `allow_ip_forwarding: true` e pool `10.42.0.0/16`, BGP + IPIP padrão); zonas `topology.kubernetes.io/zone`: server em `zona-a`, agents em `zona-b`. Plataforma, overlay e imagens como na seção anterior (`validacao/producao-k3d`), `ConsultarNfse` em `novo` 100% no CR e `ConsultarNfseServicoTomado` em `legado`. Carga: k6 na rede do compose, pelo ingress-nginx (2 réplicas), 10 req/s de `ConsultarNfse` + 1 req/s de `ServicoTomado`; cada falha logada com hora e erro.

```bash
k3d cluster create nfse-calico --servers 1 --agents 2 --network desafio-dotnet-senior-modernizacao_default --k3s-arg "--flannel-backend=none@server:*" --k3s-arg "--disable-network-policy@server:*" --k3s-arg "--disable=traefik@server:*" --kubeconfig-update-default=false --kubeconfig-switch-context=false
```

Sem flannel, o k3s não escreve `bin_dir`/`conf_dir` de CNI no containerd, que usa os padrões `/opt/cni/bin` e `/etc/cni/net.d`: o `calico.yaml` oficial funciona sem trocar caminhos. Antes dos drills, conferido que o Calico aplica as políticas: um pod em `default` não chega à fachada nem ao nfse-novo na 8080 (timeout); do namespace `ingress-nginx`, a fachada responde 200.

| Cenário | Resultado |
| --- | --- |
| Espalhamento inicial | fachada, nfse-novo (3 pelo HPA) e flagd (2): uma réplica por nó; por zona, 1 em `zona-a` e 2 em `zona-b` |
| Restart (`spec.restartAt`) da fachada sob carga, 2 vezes, **com** as NetworkPolicies | **0 de 1.631** e **0 de 1.636** falhas; 3 pods trocados em 15–31 s, de novo um por nó. Com kube-router tinham sido 2 de 662 `connection refused` (lá um `rollout restart` de Deployment no overlay local; aqui o restart do Argo, que troca os pods um a um; nos dois casos o IP do pod novo entra no Service assim que fica pronto) |
| Restart do nfse-novo sob carga, 2 vezes | **0 de 1.650** na segunda rodada. Na primeira, 26 de 1.623 falharam, todas em `ServicoTomado` por timeout de 30 s do k6; essa operação estava em `legado` e não passa pelo nfse-novo: foi o legado/Oracle lento (mediana de 19 s na rodada), não o restart. Nenhum `connection refused` |
| Drain de um agent sob carga, **antes** da correção | drain em 8 s respeitando os PDBs, **0 de 1.652** falhas, mas o substituto da fachada e o do nfse-novo ficaram **Pending** até o uncordon (`didn't match pod topology spread constraints`): **2/3 réplicas durante toda a manutenção** |
| O mesmo drain **depois** da correção (`nodeTaintsPolicy: Honor`) | 3/3 de volta **8 s** depois do início do drain, nos 2 nós restantes (skew 2/1, dentro de `maxSkew: 1`); **0 de 1.652** falhas |
| Kill switch nível 1 (`migracao.kill-switch` no CR), 6 trocas sob carga | nas 3 réplicas, a última requisição no estado antigo saiu **≤ 0,07 s** depois do patch; a primeira no estado novo, 0,09–1,49 s depois (limitada pela chegada de requisições: ~3 req/s por réplica) |
| Canário da fachada (mudança real: a correção acima) | análise aprovou duas vezes (5xx: diferença 0; p99: 0,99× e 1,07×); promovido 34% → 67% → 100%, uma réplica por nó em cada degrau (o espalhamento por `rollouts-pod-template-hash` deixa canário e estável dividirem nó) |
| Canário do nfse-novo (mesma mudança) | **abortado pela análise duas vezes**: p99 do canário 22× e 4,3×, depois 1,70× e 1,74× o estável; 5xx: diferença 0. O Argo escalou o canário a zero sozinho; as 3 réplicas estáveis seguiram atendendo. Promovido à mão (`promote --full`) |
| Tráfego durante kill switch, canários, os 2 abortos e as promoções | k6: **0 de 12.588** falhas |

**Correção (commit `fix(deploy)`):** sem `nodeTaintsPolicy`, o padrão é `Ignore`: o nó em cordon continua contando como domínio do espalhamento por nó, com 0 pods, e pôr a 3ª réplica num dos outros dois daria skew 2. Com réplicas = nós (3 em produção) e `DoNotSchedule`, qualquer drain deixava a fachada e o nfse-novo com 2/3 até o uncordon. `Honor` tira da conta o nó com o taint `unschedulable`. Vale nas duas restrições (nó e zona) da fachada e do nfse-novo.

**Falso positivo da análise do nfse-novo.** O binário era o mesmo; só mudou o agendamento. A mediana do canário e a do estável eram iguais (2,5 ms), mas: (1) o canário recebeu 0,8 req/s, contra 1,7–4,8 req/s de cada réplica estável (a fachada recicla conexões a cada 1 min, e a divisão por conexão fica desigual a poucas req/s); (2) com mediana de 2,5 ms, 1,2× no p99 é cerca de 1 ms, e três réplicas **estáveis** idênticas tinham p99 de 5, 5 e 16 ms na mesma janela. A essa taxa, a variação entre pods iguais é maior que a margem do plano. Não mudei o limite (é critério do plano, §6.1); proposta: exigir um mínimo de requisições do canário na janela (inconclusivo abaixo disso) ou um piso absoluto de diferença no p99, a decidir com o plano. Com tráfego de produção a janela de 5 min tem amostras suficientes e o problema tende a sumir, mas o aborto em horário de pouco tráfego é provável.

Limites: o k3d põe os 3 "nós" na mesma VM do Docker, então zona é só rótulo (sem latência entre zonas, sem perda de zona); o ingress-nginx recebeu a carga pelo IP de um nó só; durante a montagem, outras stacks na mesma VM esgotaram a memória e o cluster ficou parado ~15 min (os números acima são de depois, com a VM sem pressão). O cluster foi apagado no fim.

