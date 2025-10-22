using Faster.MessageBus.Contracts;
using Faster.MessageBus.Features.Commands;
using Faster.MessageBus.Features.Commands.Contracts;
using Faster.MessageBus.Features.Discovery.Contracts;
using Faster.MessageBus.Features.Events;
using Faster.MessageBus.Features.Events.Contracts;
using Faster.MessageBus.Features.Heartbeat.Contracts;
using Faster.MessageBus.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// The main, unified entry point for the entire message bus system.
/// </summary>
/// <remarks>
/// This class serves two primary purposes:
/// 1. As a façade, it provides a single, injectable access point to the core functionalities of the message bus:
///    the <see cref="EventDispatcher"/> for publish-subscribe messaging and the <see cref="CommandDispatcher"/> for request-reply messaging.
/// 2. As an <see cref="IServiceInstaller"/>, it handles its own registration with the dependency injection container.
/// <example>
/// Usage in a service:
/// <code>
/// public class MyService
/// {
///     private readonly IMessageBroker _messageBus;
///
///     public MyService(IMessageBroker messageBus)
///     {
///         _messageBus = messageBus;
///     }
///
///     public async Task DoSomething()
///     {
///         // Publish an event
///         _messageBus.EventDispatcher.Publish(new MyEvent());
///
///         // StreamAsync a command and get a reply
///         var response = await _messageBus.CommandDispatcher.Local.StreamAsync(topic, command, timeout);
///     }
/// }
/// </code>
/// </example>
/// </remarks>
public class MessageBroker : IMessageBroker
{
    #region Fields

    private readonly IOptions<MessageBrokerOptions> _options;

    #endregion

    #region Properties

    /// <summary>
    /// Gets the dispatcher for publishing events using the publish-subscribe pattern.
    /// </summary>
    /// <value>The <see cref="IEventDispatcher"/> implementation.</value>
    public IEventDispatcher EventDispatcher { get; }

    /// <summary>
    /// Gets the dispatcher for sending commands using the request-reply pattern across various scopes.
    /// </summary>
    /// <value>The <see cref="ICommandDispatcher"/> implementation.</value>
    public ICommandDispatcher CommandDispatcher { get; }

    #endregion  

    #region Ctor

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageBroker"/> class,
    /// sets up command routing, bloom filters, and starts discovery.
    /// </summary>
    /// <param name="eventDispatcher">The concrete event dispatcher implementation.</param>
    /// <param name="commandDispatcher">The concrete command dispatcher implementation.</param>
    /// <param name="serviceProvider">The service provider for resolving dependencies.</param>
    /// <param name="mesh">The mesh instance used for discovery initialization.</param>
    public MessageBroker(IEventDispatcher eventDispatcher,
        ICommandDispatcher commandDispatcher,
        IServiceProvider serviceProvider,
        IEventAggregator eventAggregator,
        IOptions<MessageBrokerOptions> options,
        MeshApplication mesh)
    {
        EventDispatcher = eventDispatcher;
        CommandDispatcher = commandDispatcher;
        _options = options;
        ActivateCoreServices(serviceProvider);
        var commandContext = InitializeCommandHandling(serviceProvider);
        InitializeEventHandling(serviceProvider);
        var routingFilter = InitializeRoutingFilter(serviceProvider, commandContext);
        StartNetworkDiscovery(serviceProvider, routingFilter, mesh, eventAggregator);
    }

    #endregion

    #region Methods

    /// <summary>
    /// Ensures that essential singleton services are activated during application startup.
    /// This includes starting the command server host (primary + scale-out instances)
    /// and initializing the heartbeat monitor for health tracking.
    /// </summary>
    /// <param name="serviceProvider">The application's dependency injection service provider.</param>
    private void ActivateCoreServices(IServiceProvider serviceProvider)
    {
        // Initialize and scale out the command server host
        var serverHost = serviceProvider.GetRequiredService<ICommandServerHost>();
        serverHost.Initialize();

        // Scale out additional command server instances (if configured).
        // This ensures the message bus can process more commands in parallel by running
        // multiple Router/Dealer loops, reducing the chance of bottlenecks on a single server.
        serverHost.ScaleOut();

        // Activate heartbeat monitoring to track service health
        serviceProvider.GetRequiredService<IHeartBeatMonitor>();
    }

    /// <summary>
    /// Scans for all command types and initializes the command handler provider.
    /// </summary>
    /// <returns>A list of the discovered command type contexts.</returns>
    private List<(Type messageType, Type responseType)> InitializeCommandHandling(IServiceProvider serviceProvider)
    {
        var scanner = serviceProvider.GetRequiredService<ICommandScanner>();
        var handler = serviceProvider.GetRequiredService<ICommandHandlerProvider>();

        var commandContext = scanner.ScanForCommands().ToList();
        handler.Initialize(commandContext);

        return commandContext;
    }



    /// <summary>
    /// Scans for all event types and initializes the event handler provider.
    /// </summary>
    private void InitializeEventHandling(IServiceProvider serviceProvider)
    {
        var scanner = serviceProvider.GetRequiredService<IEventScanner>();
        var handler = serviceProvider.GetRequiredService<IEventHandlerProvider>();

        // Note: Renamed variable to 'eventTypes' for clarity.
        var eventTypes = scanner.ScanForEvents().ToList();
        handler.Initialize(eventTypes);
    }

    /// <summary>
    /// Initializes and populates the command routing filter.
    /// </summary>
    /// <returns>The configured command routing filter.</returns>
    private ICommandRoutingFilter InitializeRoutingFilter(IServiceProvider serviceProvider, List<(Type messageType, Type responseType)> commandContext)
    {
        var filter = serviceProvider.GetRequiredService<ICommandRoutingFilter>();
        filter.Initialize(commandContext.Count);

        foreach (var command in commandContext)
        {
            // Note: Using FullName is more robust than Name to avoid hash collisions.
            var hash = WyHash.Hash(command.messageType.Name);
            filter.Add(hash);
        }

        return filter;
    }

    /// <summary>
    /// Configures the mesh context and starts the network discovery service.
    /// </summary>
    private void StartNetworkDiscovery(IServiceProvider serviceProvider, ICommandRoutingFilter routingFilter, MeshApplication mesh, IEventAggregator eventAggregator)
    {
        var context = mesh.GetMeshContext(routingFilter.GetMembershipTable());

        // determine if we scaled out, mostly due to performance reasons
        context.Hosts = _options.Value.ServerInstances;

        // Start discovery after registration
        serviceProvider.GetRequiredService<IMeshDiscoveryService>().Start(context);

        context.Self = true;

        // Register self to socket managers
        eventAggregator.Publish(new MeshJoined(context));
    }

    #endregion
}

/// <summary>
/// Handles dependency injection registration for the message bus.
/// </summary>
public class ServiceInstaller : IServiceInstaller
{
    /// <summary>
    /// Registers the message bus services with the dependency injection container.
    /// </summary>
    /// <param name="serviceCollection">The service collection to register services into.</param>
    public void Install(IServiceCollection serviceCollection)
    {
        // Registers this entry point as a singleton, making IMessageBroker
        // available for injection throughout the application.
        serviceCollection.AddSingleton<IMessageBroker, MessageBroker>();
        serviceCollection.AddSingleton<ICommandDispatcher, CommandDispatcher>();
        serviceCollection.AddSingleton<IEventDispatcher, EventDispatcher>();
        serviceCollection.AddSingleton<IEventAggregator, EventAggregator>();
    }
}
