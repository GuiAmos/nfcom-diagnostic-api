using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() 
    { 
        Title = "NFCom (Modelo 62) - Agente de Diagnostico SEFAZ", 
        Version = "v1",
        Description = "API para diagnostico explicativo e passo a passo de correcao de erros da NFCom."
    });
});

builder.Services.AddSingleton<NfcomDiagnosticService>();

var app = builder.Build();

// Configurações para servir a interface HTML/JS criada na raiz '/'
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "NFCom Diagnostic API v1");
    c.RoutePrefix = "swagger";
});

app.UseHttpsRedirection();

// Endpoint resiliente: exibe os campos no Swagger e tolera aspas da SEFAZ no JSON
app.MapPost("/api/sefaz/analisar", async (
    HttpContext httpContext, 
    [FromServices] NfcomDiagnosticService diagnosticService) =>
{
    using var reader = new StreamReader(httpContext.Request.Body);
    var rawBody = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(rawBody))
    {
        return Results.BadRequest(new { mensagem = "O corpo da requisicao esta vazio." });
    }

    rawBody = rawBody.Replace('\u00A0', ' ').Replace("&nbsp;", " ");

    SefazAnalysisRequest? request = null;

    try
    {
        request = JsonSerializer.Deserialize<SefazAnalysisRequest>(rawBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
    }
    catch
    {
        request = RecuperarRequisicaoComAspasQuebradas(rawBody);
    }

    if (request == null || request.CStat <= 0)
    {
        request = RecuperarRequisicaoComAspasQuebradas(rawBody);
    }

    if (request == null || request.CStat <= 0)
    {
        return Results.BadRequest(new { mensagem = "Nao foi possivel identificar o codigo cStat retornado pela SEFAZ." });
    }

    var diagnostico = diagnosticService.Diagnosticar(request);
    return Results.Ok(diagnostico);
})
.Accepts<SefazAnalysisRequest>("application/json")
.WithName("AnalisarRejeicaoSefaz")
.WithSummary("Analisa rejeicoes da NFCom Modelo 62 e fornece instrucao manual de correcao")
.Produces<SefazDiagnosticResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest);

app.Run();

// ==========================================
// PARSER RESILIENTE PARA SEFAZ
// ==========================================

static SefazAnalysisRequest? RecuperarRequisicaoComAspasQuebradas(string raw)
{
    try
    {
        int cStat = 0;
        var matchCStat = Regex.Match(raw, @"[\""']?cStat[\""']?[\s\u00A0]*:[\s\u00A0]*(\d+)", RegexOptions.IgnoreCase);
        if (matchCStat.Success)
        {
            int.TryParse(matchCStat.Groups[1].Value, out cStat);
        }

        string xMotivo = "";
        var matchMotivo = Regex.Match(raw, @"[\""']?xMotivo[\""']?[\s\u00A0]*:[\s\u00A0]*[\""']?([\s\S]*?)[\""']?[\s\u00A0]*,[\s\u00A0]*[\""']?(?:jsonEnvio|xmlContent)", RegexOptions.IgnoreCase);
        if (matchMotivo.Success)
        {
            xMotivo = matchMotivo.Groups[1].Value.Trim().TrimEnd('"').Trim();
        }

        JsonElement? jsonEnvio = null;
        var idxJson = raw.IndexOf("\"jsonEnvio\"", StringComparison.OrdinalIgnoreCase);
        if (idxJson >= 0)
        {
            var idxStart = raw.IndexOf('{', idxJson);
            var idxEnd = raw.LastIndexOf('}');
            if (idxStart >= 0 && idxEnd > idxStart)
            {
                var jsonSub = raw.Substring(idxStart, idxEnd - idxStart + 1);
                try
                {
                    jsonEnvio = JsonDocument.Parse(jsonSub).RootElement;
                }
                catch { }
            }
        }

        return new SefazAnalysisRequest
        {
            CStat = cStat,
            XMotivo = xMotivo,
            JsonEnvio = jsonEnvio
        };
    }
    catch
    {
        return null;
    }
}

// ==========================================
// MODELOS DE DADOS
// ==========================================

public class SefazAnalysisRequest
{
    public int CStat { get; set; }
    public string? XMotivo { get; set; }
    public JsonElement? JsonEnvio { get; set; }
    public string? XmlContent { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticSeverity
{
    Cadastro,
    CalculoTributario,
    RegraNegocio,
    ConfiguracaoAmbiente
}

public class SefazDiagnosticResult
{
    public int StatusCode { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public DiagnosticSeverity Severidade { get; set; }
    public string CausaProvavel { get; set; } = string.Empty;
    public List<string> PassosParaCorrecao { get; set; } = new();
    public string? ExemploXmlEsperado { get; set; }
    public List<string>? DivergenciasDetectadas { get; set; }
}

// ==========================================
// MOTOR DE DIAGNOSTICO E REGRAS NFCOM
// ==========================================

public class NfcomDiagnosticService
{
    public SefazDiagnosticResult Diagnosticar(SefazAnalysisRequest request)
    {
        JsonNode? json = null;
        if (request.JsonEnvio.HasValue && request.JsonEnvio.Value.ValueKind != JsonValueKind.Null && request.JsonEnvio.Value.ValueKind != JsonValueKind.Undefined)
        {
            try
            {
                json = JsonNode.Parse(request.JsonEnvio.Value.GetRawText());
            }
            catch { }
        }

        XDocument? doc = null;
        if (!string.IsNullOrWhiteSpace(request.XmlContent) && request.XmlContent.Trim().StartsWith("<"))
        {
            try { doc = XDocument.Parse(request.XmlContent); } catch { }
        }

        return request.CStat switch
        {
            215 => Diagnosticar215(request.XMotivo),
            256 => Diagnosticar256(json),
            257 => Diagnosticar257(json),
            431 => Diagnosticar431(doc),
            432 => Diagnosticar432(),
            426 => Diagnosticar426(),
            420 => Diagnosticar420(),
            435 => Diagnosticar435(doc),
            447 => Diagnosticar447(),
            460 => Diagnosticar460(),
            537 => Diagnosticar537(),
            540 => Diagnosticar540(),
            541 => Diagnosticar541(),
            204 or 539 => Diagnosticar204(request.CStat),

            _ => new SefazDiagnosticResult
            {
                StatusCode = request.CStat,
                Titulo = $"Rejeicao {request.CStat}: {request.XMotivo ?? "Nao mapeada especificamente"}",
                Severidade = DiagnosticSeverity.RegraNegocio,
                CausaProvavel = request.XMotivo ?? "Verifique a documentacao do MOC NFCom Modelo 62.",
                PassosParaCorrecao = new List<string>
                {
                    "Consulte o Anexo I do MOC da NFCom para identificar as tags da regra violada.",
                    "Valide os campos enviados contra a estrutura vigente."
                }
            }
        };
    }

    private static SefazDiagnosticResult Diagnosticar215(string? motivo)
    {
        var divergencias = new List<string>();
        string causa = "O XML gerado nao esta em conformidade com o arquivo de validacao (.xsd) oficial da SEFAZ para a NFCom Modelo 62.";
        var passos = new List<string>
        {
            "Verifique a mensagem de erro especifica do schema retornada pela SEFAZ.",
            "Consulte a ordem estrita dos elementos no Anexo I do MOC da NFCom.",
            "Certifique-se de que tags numericas vazias sejam omitidas em vez de enviadas com espacos ou nulas."
        };

        if (!string.IsNullOrEmpty(motivo) && motivo.Contains("gIBS") && motivo.Contains("vBCIBSCBS"))
        {
            causa = "Divergencia na estrutura dos tributos da Reforma Tributaria (IBS/CBS). O schema XSD espera a base de calculo (<vBCIBSCBS>) antes da tag de detalhamento (<gIBS>).";
            passos = new List<string>
            {
                "No gerador de XML/JSON do seu ERP, verifique o grupo de IBS/CBS.",
                "Se a operacao for isenta ou nao tributada por IBS/CBS e os valores forem 0.00, verifique se esses campos de totais da reforma (vBCIBSCBS_total, vDevTrib_total) devem ser totalmente omitidos da nota.",
                "Se as tags forem necessarias, certifique-se de que a tag <vBCIBSCBS> venha declarada estritamente antes do no <gIBS>."
            };
            divergencias.Add("O validador XML encontrou '<gIBS>', mas esperava o elemento '<vBCIBSCBS>' antes dele.");
        }

        return new SefazDiagnosticResult
        {
            StatusCode = 215,
            Titulo = "Rejeicao 215: Falha no schema XML",
            Severidade = DiagnosticSeverity.ConfiguracaoAmbiente,
            CausaProvavel = causa,
            PassosParaCorrecao = passos,
            DivergenciasDetectadas = divergencias.Count > 0 ? divergencias : null
        };
    }

    private static SefazDiagnosticResult Diagnosticar256(JsonNode? json)
    {
        var divergencias = new List<string>();

        if (json != null)
        {
            var vFustTotalStr = json["NFCOM"]?["INCLUIRNFCOM"]?[0]?["vFUST_total"]?.ToString();
            var itens = json["NFCOM"]?["INCLUIRDET"]?.AsArray();

            if (itens != null && decimal.TryParse(vFustTotalStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var vFustTotal))
            {
                decimal somaFustItens = 0;
                foreach (var item in itens)
                {
                    var fustItemStr = item?["vFUST"]?.ToString();
                    if (decimal.TryParse(fustItemStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var fustVal))
                    {
                        somaFustItens += fustVal;
                    }
                }

                if (vFustTotal != somaFustItens)
                {
                    divergencias.Add($"Divergencia detectada no JSON: 'vFUST_total' do cabecalho e {vFustTotal:F2}, mas a soma de 'vFUST' dos itens resulta em {somaFustItens:F2}.");
                }
            }
        }

        return new SefazDiagnosticResult
        {
            StatusCode = 256,
            Titulo = "Rejeicao 256: Total do FUST difere do somatorio dos itens",
            Severidade = DiagnosticSeverity.CalculoTributario,
            CausaProvavel = "O totalizador geral do FUST (vFUST_total) no cabecalho difere da soma exata dos valores de FUST informados nos itens individuais.",
            PassosParaCorrecao = new List<string>
            {
                "Some o campo 'vFUST' de todos os itens da nota.",
                "Ajuste o campo 'vFUST_total' do cabecalho para coincidir com a soma dos itens.",
                "Verifique o arredondamento: garanta que o item e o cabecalho usem o mesmo valor com duas casas decimais."
            },
            DivergenciasDetectadas = divergencias.Count > 0 ? divergencias : null
        };
    }

    private static SefazDiagnosticResult Diagnosticar257(JsonNode? json) => new()
    {
        StatusCode = 257,
        Titulo = "Rejeicao 257: Total do FUNTTEL difere do somatorio dos itens",
        Severidade = DiagnosticSeverity.CalculoTributario,
        CausaProvavel = "O totalizador de FUNTTEL no cabecalho diverge da soma exata de vFUNTTEL presente nos itens faturados.",
        PassosParaCorrecao = new List<string>
        {
            "Some o campo 'vFUNTTEL' informado em cada item.",
            "Atualize o valor de 'vFUNTTEL_total' no cabecalho com esse somatorio exato."
        }
    };

    private static SefazDiagnosticResult Diagnosticar431(XDocument? doc) => new()
    {
        StatusCode = 431,
        Titulo = "Rejeicao 431: IE nao pode ser informada para tomador nao contribuinte",
        Severidade = DiagnosticSeverity.Cadastro,
        CausaProvavel = "A tag <indIEDest> foi enviada com valor '9' (Nao Contribuinte), mas a tag <IE> foi informada no no <dest>.",
        PassosParaCorrecao = new List<string>
        {
            "Abra o cadastro do assinante/destinatario no ERP.",
            "Remova a Inscricao Estadual para que o XML nao gere a tag <dest><IE>.",
            "Se o tomador for contribuinte de fato, altere o indicador para '1' (Contribuinte ICMS)."
        },
        ExemploXmlEsperado = "<dest><xNome>NOME DO TOMADOR</xNome><CPF>00000000000</CPF><indIEDest>9</indIEDest></dest>"
    };

    private static SefazDiagnosticResult Diagnosticar432() => new()
    {
        StatusCode = 432,
        Titulo = "Rejeicao 432: UF do Destinatario nao aceita ISENTO com Inscricao Estadual",
        Severidade = DiagnosticSeverity.Cadastro,
        CausaProvavel = "O cliente foi indicado com <indIEDest>=2 (Isento), mas a UF de destino nao aceita ISENTO.",
        PassosParaCorrecao = new List<string>
        {
            "Altere o indicador de Inscricao Estadual (<indIEDest>) para '9' (Nao Contribuinte).",
            "Remova qualquer texto de IE e gere a nota novamente."
        },
        ExemploXmlEsperado = "<dest><indIEDest>9</indIEDest></dest>"
    };

    private static SefazDiagnosticResult Diagnosticar426() => new()
    {
        StatusCode = 426,
        Titulo = "Rejeicao 426: IE deve ser informada para tomador Contribuinte",
        Severidade = DiagnosticSeverity.Cadastro,
        CausaProvavel = "Foi informado <indIEDest>=1, mas a tag <IE> nao foi preenchida ou recebeu 'ISENTO'.",
        PassosParaCorrecao = new List<string>
        {
            "Preencha uma Inscricao Estadual valida ou altere para '9' (Nao Contribuinte)."
        }
    };

    private static SefazDiagnosticResult Diagnosticar420() => new()
    {
        StatusCode = 420,
        Titulo = "Rejeicao 420: IE do tomador isento deve ser preenchida com 'ISENTO'",
        Severidade = DiagnosticSeverity.Cadastro,
        CausaProvavel = "Foi informado <indIEDest>=2, mas a tag <IE> nao contem o literal 'ISENTO'.",
        PassosParaCorrecao = new List<string>
        {
            "Para tomador Isento em UFs que aceitam, a IE deve ser 'ISENTO' ou altere para indIEDest=9."
        }
    };

    private static SefazDiagnosticResult Diagnosticar435(XDocument? doc) => new()
    {
        StatusCode = 435,
        Titulo = "Rejeicao 435: Valor do item difere de (vItem * qFaturada) - vDesc + vOutro",
        Severidade = DiagnosticSeverity.CalculoTributario,
        CausaProvavel = "A tag <vProd> difere de (qFaturada * vItem) - vDesc + vOutro.",
        PassosParaCorrecao = new List<string>
        {
            "Valide a formula do item faturado e verifique se o ERP esta arredondando com 2 casas decimais."
        },
        ExemploXmlEsperado = "<prod><qFaturada>1.0000</qFaturada><vItem>100.00</vItem><vDesc>10.00</vDesc><vProd>90.00</vProd></prod>"
    };

    private static SefazDiagnosticResult Diagnosticar447() => new()
    {
        StatusCode = 447,
        Titulo = "Rejeicao 447: Total da base de calculo difere do somatorio dos itens",
        Severidade = DiagnosticSeverity.CalculoTributario,
        CausaProvavel = "O campo <total><ICMSTot><vBC> nao bate com a soma dos itens.",
        PassosParaCorrecao = new List<string>
        {
            "Some o valor da base de calculo de cada item e atualize o cabecalho no ERP."
        }
    };

    private static SefazDiagnosticResult Diagnosticar460() => new()
    {
        StatusCode = 460,
        Titulo = "Rejeicao 460: Total da NFCom difere dos valores componentes",
        Severidade = DiagnosticSeverity.CalculoTributario,
        CausaProvavel = "O total da nota difere de (+) vProd (-) retencoes federais.",
        PassosParaCorrecao = new List<string>
        {
            "Some os produtos e deduza as retencoes federais cadastradas."
        }
    };

    private static SefazDiagnosticResult Diagnosticar537() => new()
    {
        StatusCode = 537,
        Titulo = "Rejeicao 537: Dados do contrato sao obrigatorios para faturamento normal e centralizado",
        Severidade = DiagnosticSeverity.RegraNegocio,
        CausaProvavel = "Para emissao Normal (tpFat=0) ou Centralizado (tpFat=1), os campos de contrato sao obrigatorios.",
        PassosParaCorrecao = new List<string>
        {
            "Preencha o numero do contrato (<nContrato>) e a data de inicio (<dContratoIni>)."
        },
        ExemploXmlEsperado = "<assinante><iCodAssinante>12345</iCodAssinante><tpAssinante>1</tpAssinante><tpServUtil>4</tpServUtil><nContrato>CTR-2026-01</nContrato><dContratoIni>2026-01-10</dContratoIni></assinante>"
    };

    private static SefazDiagnosticResult Diagnosticar540() => new()
    {
        StatusCode = 540,
        Titulo = "Rejeicao 540: CFOP e obrigatorio para item com CST informado",
        Severidade = DiagnosticSeverity.RegraNegocio,
        CausaProvavel = "O item possui CST de ICMS, mas o CFOP nao foi informado.",
        PassosParaCorrecao = new List<string>
        {
            "Preencha o CFOP correspondente ao servico faturado."
        }
    };

    private static SefazDiagnosticResult Diagnosticar541() => new()
    {
        StatusCode = 541,
        Titulo = "Rejeicao 541: Vedada indicacao de CFOP para item sem CST",
        Severidade = DiagnosticSeverity.RegraNegocio,
        CausaProvavel = "Foi informada a tag <indSemCST>, mas o item possui CFOP preenchido.",
        PassosParaCorrecao = new List<string>
        {
            "Remova o CFOP do item se usar <indSemCST>, ou remova <indSemCST> e preencha o ICMS."
        }
    };

    private static SefazDiagnosticResult Diagnosticar204(int cStat) => new()
    {
        StatusCode = cStat,
        Titulo = $"Rejeicao {cStat}: Duplicidade de NFCom",
        Severidade = DiagnosticSeverity.RegraNegocio,
        CausaProvavel = "Esta nota ja foi autorizada na SEFAZ.",
        PassosParaCorrecao = new List<string>
        {
            "Sincronize a nota como Autorizada usando o protocolo original da SEFAZ."
        }
    };
}