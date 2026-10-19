"""Compara dois resumos do k6 (--summary-export) com o critério do plano de migração (§6.1).

uso: python3 tests/carga/comparar.py legado.json novo.json
"""
import json
import sys

LIMITE_P99 = 1.2  # [H] plano de migração, §6.1: p99 do novo ≤ 1,2 × p99 do legado


def metricas(caminho):
    with open(caminho, encoding="utf-8") as arquivo:
        dados = json.load(arquivo)["metrics"]
    resultado = {}
    for operacao in ("ConsultarNfse", "ConsultarNfseServicoTomado"):
        trend = dados[f"http_req_duration{{operacao:{operacao}}}"]
        resultado[operacao] = {k: trend[k] for k in ("med", "p(95)", "p(99)", "max")}
    resultado["falhas"] = dados["http_req_failed"]["value"]
    resultado["requisicoes"] = dados["http_reqs"]["count"]
    return resultado


def main():
    legado, novo = metricas(sys.argv[1]), metricas(sys.argv[2])
    print(f"requisições: legado {legado['requisicoes']}, novo {novo['requisicoes']}")
    print(f"taxa de falha: legado {legado['falhas']:.2%}, novo {novo['falhas']:.2%}")
    print()
    print(f"{'operação':28} {'métrica':6} {'legado (ms)':>12} {'novo (ms)':>10} {'novo/legado':>12}")
    aprovado = True
    for operacao in ("ConsultarNfse", "ConsultarNfseServicoTomado"):
        for metrica in ("med", "p(95)", "p(99)"):
            l, n = legado[operacao][metrica], novo[operacao][metrica]
            razao = n / l if l else float("inf")
            print(f"{operacao:28} {metrica:6} {l:12.1f} {n:10.1f} {razao:12.2f}")
        if novo[operacao]["p(99)"] > LIMITE_P99 * legado[operacao]["p(99)"]:
            aprovado = False
    print()
    print(f"critério do plano (p99 do novo ≤ {LIMITE_P99} × p99 do legado): {'atende' if aprovado else 'NÃO atende'}")
    return 0 if aprovado else 1


if __name__ == "__main__":
    sys.exit(main())
