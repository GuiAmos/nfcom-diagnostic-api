# 📑 NFCom Diagnostic API & Web UI (Modelo 62)

Aplicação desenvolvida em **C# / .NET 10 (ASP.NET Core Minimal API)** com interface gráfica nativa para diagnóstico inteligente e instrução manual de correção de rejeições da **Nota Fiscal Fatura de Serviços de Comunicação Eletrônica (NFCom - Modelo 62)**, com base nas regras do **MOC (Manual de Orientação do Contribuinte - Anexo I e II)**.

---

## 🎯 Objetivo

O objetivo deste agente é dar **agilidade no tratamento de erros da SEFAZ** para emissores multiempresas:
- **Diagnóstico Claro:** Converte códigos técnicos (`cStat`) e mensagens da SEFAZ em explicações objetivas sobre a causa raiz do problema.
- **Passo a Passo de Resolução:** Fornece as instruções exatas do que alterar manualmente no cadastro ou no ERP.
- **Detecção Automática de Divergências:** Ao informar o payload (JSON ou XML), o motor inspeciona cabeçalhos e itens para encontrar diferenças de arredondamento, somatórios inconsistentes ou conflitos de schema.
- **Parser Resiliente:** Trata automaticamente retornos da SEFAZ com caracteres especiais, aspas não escapadas e espaços invisíveis (*Non-Breaking Spaces*).

---

## 🚀 Principais Rejeições Mapeadas

* **Schema XML / Reforma Tributária:**
  * `cStat 215`: Falha de validação de schema XSD (deteta tags de IBS/CBS fora de ordem ou falta de base unificada `vBCIBSCBS`).
* **Totais e Tributos Federais:**
  * `cStat 256`: Total do FUST difere do somatório dos itens (`vFUST_total` vs `vFUST`).
  * `cStat 257`: Total do FUNTTEL difere do somatório dos itens (`vFUNTTEL_total` vs `vFUNTTEL`).
  * `cStat 447`: Total da base de cálculo difere do somatório dos itens.
  * `cStat 460`: Total da NFCom difere da equação de composição líquida.
* **Cálculos e Itens:**
  * `cStat 435`: Divergência entre `vProd` e `(vItem * qFaturada) - vDesc + vOutro`.
* **Cadastro e Inscrição Estadual:**
  * `cStat 420`: IE de tomador isento não preenchida como `ISENTO`.
  * `cStat 426`: IE obrigatória para tomador contribuinte ICMS (`indIEDest=1`).
  * `cStat 431`: IE indevida para tomador não contribuinte (`indIEDest=9`).
  * `cStat 432`: UF do destinatário rejeita a condição `ISENTO`.
* **Regras do Modelo 62 / Telecomunicações:**
  * `cStat 537`: Obrigatoriedade de dados contratuais (`nContrato` e `dContratoIni`) para faturamento normal ou centralizado.
  * `cStat 540` / `cStat 541`: Validação de preenchimento ou vedação de CFOP quando usada a flag `indSemCST`.
* **Duplicidade:**
  * `cStat 204` / `cStat 539`: Identificação de notas já autorizadas com instruções de sincronização de protocolo.

---

## 🛠️ Tecnologias Utilizadas

* **.NET 10 SDK** (C#)
* **ASP.NET Core Minimal APIs**
* **Swashbuckle / OpenAPI** (Swagger UI integrado)
* **HTML5 / CSS3 Moderno / Vanilla JS** (Interface visual sem dependências externas)

---

## 📂 Estrutura do Projeto

* Properties/ -> launchSettings.json
* wwwroot/ -> index.html (Interface Web: Painel de Parâmetros e Diagnóstico)
* Program.cs -> API Minimal, Modelos, Parser Resiliente e Motor de Regras
* SefazErrorAgent.csproj -> Ficheiro de configuração e dependências .NET
* .gitignore -> Ficheiros ignorados pelo Git (bin, obj, etc.)
* README.md -> Documentação do projeto

---

## ⚙️ Pré-requisitos

* .NET SDK 10 instalado na máquina.
* Visual Studio Code ou qualquer terminal compatível.

---

## 🏃 Como Executar

1. Clone o repositório:
   git clone <URL_DO_SEU_REPOSITORIO>
   cd "NFCom Diagnostic API Application"

2. Restaure as dependências:
   dotnet restore

3. Execute a aplicação:
   dotnet run

4. Aceda às interfaces no navegador:
   * Interface Web (Principal): http://localhost:5218/
   * Documentação Swagger: http://localhost:5218/swagger

---

## 🖥️ Como Utilizar a Interface

1. Código SEFAZ (cStat): Insira o código da rejeição retornado pelo integrador (ex.: 256, 215, 537).
2. Mensagem de Retorno (xMotivo): (Opcional) Cole a mensagem completa da SEFAZ. O motor identifica trechos complexos de falhas de schema.
3. Payload Enviado: (Opcional) Cole o JSON ou XML rejeitado.
4. Clique em "Analisar Erro": O painel lateral exibirá a Categoria, a Causa Raiz, as Divergências Numéricas e o Passo a Passo para ajuste no ERP.
5. Clique em "Limpar": Zera todos os campos para iniciar uma nova análise.

---

## 📌 Exemplo de Chamada via API (cURL)

curl -X 'POST' \
  'http://localhost:5218/api/sefaz/analisar' \
  -H 'Content-Type: application/json' \
  -d '{
    "cStat": 256,
    "xMotivo": "Rejeição: Total do FUST difere do somatório dos itens",
    "jsonEnvio": {
      "NFCOM": {
        "INCLUIRNFCOM": [{ "vFUST_total": "0.24" }],
        "INCLUIRDET": [{ "vFUST": "0.23" }]
      }
    }
  }'

Exemplo de Resposta:
{
  "statusCode": 256,
  "titulo": "Rejeicao 256: Total do FUST difere do somatorio dos itens",
  "severidade": "CalculoTributario",
  "causaProvavel": "O totalizador geral do FUST (vFUST_total) no cabecalho difere da soma exata dos valores de FUST informados nos itens individuais.",
  "passosParaCorrecao": [
    "Some o campo vFUST de todos os itens da nota.",
    "Ajuste o campo vFUST_total do cabecalho para coincidir com a soma dos itens.",
    "Verifique o arredondamento: garanta que o item e o cabecalho usem o mesmo valor com duas casas decimais."
  ],
  "exemploXmlEsperado": null,
  "divergenciasDetectadas": [
    "Divergencia detectada no JSON: vFUST_total do cabecalho e 0.24, mas a soma de vFUST dos itens resulta em 0.23."
  ]
}

---

## 📄 Licença

Distribuído sob licença interna para diagnóstico e integração técnica com a SEFAZ.