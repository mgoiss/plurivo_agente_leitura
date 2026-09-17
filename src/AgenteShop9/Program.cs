using AgenteShop9;
using AgenteShop9.Catalogos;
using AgenteShop9.Configuracao;
using AgenteShop9.Operacoes;

var caminhoConfig = CaminhoConfiguracao.Resolver(args);

var builder = Host.CreateApplicationBuilder(args);

// Faz o processo se comportar como serviço do Windows quando instalado como tal.
// Fora do Windows (dev) isto e um no-op e ele roda como app de console.
builder.Services.AddWindowsService(options => options.ServiceName = "AgenteShop9");

builder.Services.AddSingleton(sp =>
{
    var log = sp.GetService<ILoggerFactory>()?.CreateLogger("AgenteShop9.Configuracao");
    log?.LogInformation("Lendo configuracao de: {Caminho}", caminhoConfig);
    return LeitorConfiguracao.Ler(caminhoConfig, log);
});

builder.Services.AddSingleton(new OpcoesAgente());
builder.Services.AddSingleton<GestorCatalogo>();
builder.Services.AddHttpClient<BaixadorCatalogo>();
builder.Services.AddSingleton<IExecutorOperacao, ExecutorOperacao>();
builder.Services.AddHostedService<TrabalhadorAgente>();

var host = builder.Build();

try
{
    host.Run();
}
catch (ConfiguracaoInvalidaException)
{
    // Erro de configuracao (arquivo faltando, campo invalido, URL errada): o
    // host ja registrou a mensagem no log. Aqui so evitamos o crash bruto
    // (dump do Windows) e saimos com codigo != 0.
    Environment.ExitCode = 1;
}
