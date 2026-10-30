using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Servired.Indicadores.Eventos;

public class OpcionesRabbitMq
{
    /// <summary><c>RABBITMQ_URL</c>, p. ej. <c>amqp://guest:guest@rabbitmq:5672</c>. Vacío = consumidor apagado.</summary>
    public string? Url { get; set; }
    public string Exchange { get; set; } = "servired.events";
    public string Cola { get; set; } = "indicadores.events";
    public string ExchangeErrores { get; set; } = "servired.dlx";
    public string ColaErrores { get; set; } = "indicadores.events.dlq";
    public string[] Bindings { get; set; } = ["reporte.*", "validacion.*", "fraude.*"];
    /// <summary>
    /// Declara exchange, colas y bindings al arrancar (idempotente). Debe coincidir con
    /// <c>servired-infra/compose/rabbitmq/definitions.json</c>; si difieren, RabbitMQ rechaza la declaración.
    /// </summary>
    public bool DeclararTopologia { get; set; } = true;
    public ushort Prefetch { get; set; } = 10;
    public int Reintentos { get; set; } = 3;
}

/// <summary>
/// Consume <c>indicadores.events</c>. Política de errores:
/// evento inválido → cola de errores (DLQ) de inmediato; error de BD transitorio → se reintenta y,
/// si persiste, se devuelve a la cola para no perder el evento; cualquier otro error → DLQ.
/// </summary>
public class ConsumidorEventos(
    IServiceScopeFactory scopes,
    IOptions<OpcionesRabbitMq> opciones,
    EstadoConsumidor estado,
    ILogger<ConsumidorEventos> log) : BackgroundService
{
    private readonly OpcionesRabbitMq o = opciones.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.Url))
        {
            log.LogWarning("RABBITMQ_URL no está configurada: el consumidor de eventos queda apagado.");
            estado.Apagado = true;
            return;
        }

        var espera = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConsumirAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                estado.Conectado = false;
                log.LogWarning(ex, "Sin conexión a RabbitMQ; reintento en {Espera}s.", espera.TotalSeconds);
                await Task.Delay(espera, ct);
                espera = TimeSpan.FromSeconds(Math.Min(espera.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task ConsumirAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(o.Url!),
            ClientProvidedName = "servired-indicadores",
            AutomaticRecoveryEnabled = true,
        };

        await using var conexion = await factory.CreateConnectionAsync(ct);
        await using var canal = await conexion.CreateChannelAsync(cancellationToken: ct);

        if (o.DeclararTopologia) await DeclararTopologiaAsync(canal, ct);
        await canal.BasicQosAsync(0, o.Prefetch, false, ct);

        var consumidor = new AsyncEventingBasicConsumer(canal);
        consumidor.ReceivedAsync += (_, ea) => ManejarAsync(canal, ea, ct);
        await canal.BasicConsumeAsync(o.Cola, autoAck: false, consumidor, ct);

        estado.Conectado = true;
        log.LogInformation("Consumiendo {Cola} en {Host}.", o.Cola, factory.Uri.Host);

        // Se mantiene vivo mientras la conexión exista; la recuperación automática cubre caídas cortas.
        var cerrada = new TaskCompletionSource();
        conexion.ConnectionShutdownAsync += (_, args) =>
        {
            if (args.Initiator != ShutdownInitiator.Application) cerrada.TrySetResult();
            return Task.CompletedTask;
        };
        await cerrada.Task.WaitAsync(ct);
        throw new InvalidOperationException("La conexión con RabbitMQ se cerró.");
    }

    private async Task DeclararTopologiaAsync(IChannel canal, CancellationToken ct)
    {
        await canal.ExchangeDeclareAsync(o.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        await canal.ExchangeDeclareAsync(o.ExchangeErrores, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);

        await canal.QueueDeclareAsync(o.ColaErrores, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await canal.QueueBindAsync(o.ColaErrores, o.ExchangeErrores, o.Cola, cancellationToken: ct);

        await canal.QueueDeclareAsync(o.Cola, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = o.ExchangeErrores,
                ["x-dead-letter-routing-key"] = o.Cola,
            },
            cancellationToken: ct);
        foreach (var binding in o.Bindings)
            await canal.QueueBindAsync(o.Cola, o.Exchange, binding, cancellationToken: ct);
    }

    private async Task ManejarAsync(IChannel canal, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        using var _ = log.BeginScope(new Dictionary<string, object?>
        {
            ["routingKey"] = ea.RoutingKey,
            ["messageId"] = ea.BasicProperties.MessageId,
            ["correlationId"] = ea.BasicProperties.CorrelationId,
        });

        for (var intento = 1; ; intento++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var procesador = scope.ServiceProvider.GetRequiredService<ProcesadorEventos>();
                var resultado = await procesador.ProcesarAsync(ea.Body, ct);

                await canal.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
                log.LogInformation("Evento {RoutingKey}: {Resultado}.", ea.RoutingKey, resultado);
                return;
            }
            catch (EventoInvalidoException ex)
            {
                log.LogError(ex, "Evento inválido; se envía a {Dlq}.", o.ColaErrores);
                await canal.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (intento < o.Reintentos)
                {
                    log.LogWarning(ex, "Error procesando el evento (intento {Intento}/{Max}).", intento, o.Reintentos);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, intento - 1)), ct);
                    continue;
                }

                var transitorio = EsTransitorio(ex);
                log.LogError(ex, transitorio
                    ? "La BD sigue fallando; el evento vuelve a la cola."
                    : "Error no recuperable; se envía a la cola de errores.");
                if (transitorio) await Task.Delay(TimeSpan.FromSeconds(5), ct);
                await canal.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: transitorio, ct);
                return;
            }
        }
    }

    private static bool EsTransitorio(Exception ex) => ex switch
    {
        NpgsqlException n => n.IsTransient,
        TimeoutException => true,
        { InnerException: { } inner } => EsTransitorio(inner),
        _ => false,
    };
}

/// <summary>Estado del consumidor para <c>/health</c>.</summary>
public class EstadoConsumidor : IHealthCheck
{
    public volatile bool Conectado;
    public volatile bool Apagado;

    // Sin broker el servicio sigue sirviendo la API: se reporta Degraded (HTTP 200), no Unhealthy.
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(
            Apagado ? HealthCheckResult.Degraded("Consumidor apagado (sin RABBITMQ_URL).")
            : Conectado ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded("Sin conexión a RabbitMQ."));
}
