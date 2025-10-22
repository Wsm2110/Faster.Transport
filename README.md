# Faster.MessageBus

![Work in Progress](https://img.shields.io/badge/status-work%20in%20progress-yellow)
![License](https://img.shields.io/badge/license-MIT-blue)

A high-performance, decentralized, peer-to-peer (P2P) messaging library for .NET, built on `NetMQ`. It enables services to form a self-organizing and self-healing communication mesh without a central broker.

> **Warning:** This project is currently under active development and should be considered experimental.  
> The API is subject to change, and it is not yet recommended for production use.

---

## ✨ Features

- ⚡ **High Performance:** Minimized allocations and reduced GC pressure using `Span<T>`, `ArrayPool<T>`, and `IBufferWriter<T>`.  
- 📢 **Event Dispatcher (Fire & Forget):** Publish events to topics with no reply, pure **one-to-many** distribution.  
- 🛠️ **Command Dispatcher (Request/Reply):** Send commands to a **single handler**, always returns a result to confirm execution.  
- 🌍 **Scoped Commands:** Dispatch commands at different scopes — **Local**, **Machine**, **Cluster**, or **Network**.  
- 🔍 **Automatic Service Discovery:** Nodes auto-discover and form a mesh, simplifying configuration.  
- ❤️ **Heartbeat Monitoring:** Built-in heartbeat messages to track node health, detect failures, and remove dead peers automatically.  
- 🔒 **Thread-Safe:** Dedicated scheduler thread for all network ops; no locks needed in application code.  
- 📦 **Efficient Serialization:** MessagePack + LZ4 compression for fast, compact serialization.  
 
---

## 📚 Core Concepts: Events and Commands

`faster.messagebus` provides a brokerless messaging solution by creating a **decentralized mesh network**. Each node (or application instance) acts as both a client and a server, connecting to a subset of other known peers. Messages are propagated intelligently through the mesh to reach their subscribers, ensuring high availability and eliminating single points of failure.

The architecture leverages the power of `NetMQ` (a pure C# port of ZeroMQ) for its extremely efficient, low-latency socket communication. Peer discovery can be handled through various strategies, with a default gossip-based protocol for zero-configuration deployments.


## 🛠️ How to Use

The following example demonstrates how to configure the message bus, define an event, create a handler, and publish the event.

### 1️⃣ Configure Services  

Register the message bus components with your dependency injection container:

```csharp

// In your Program.cs or startup configuration
services.AddMessageBus(options =>
{
    options.PublishPort = 10000; // Starting port for the publisher (default communication port)
    // ... configure other options as needed
});

var provider = builder.BuildServiceProvider();

// Resolve the message bus from DI
var messageBus = provider.GetRequiredService<IMessageBroker>();

```
Sending a Command (Request/Reply) 

```csharp

// Note: The command can be sent to different scopes depending on configuration:
// Local (same process), Machine (same host), Cluster (service cluster), or Network (any reachable node) 
await messageBus.CommandDispatcher.Local.SendAsync(
    new SubmitOrderCommand(Guid.NewGuid(), "Alice", 3, "Apples"), // Command with payload
    TimeSpan.FromSeconds(5),                                      // Timeout for reply
    CancellationToken.None                                        // Cancellation support
);

```
Publishing an Event (Fire-and-Forget) 

```csharp

await messageBus.EventDispatcher.Publish(
    new UserCreatedEvent(Guid.NewGuid(), "I AM GROOT Local"),      // Event object
    TimeSpan.FromSeconds(5),                                      // Timeout for acknowledgement
    CancellationToken.None
);

```
 Commands & Events
 
```csharp

public record UserCreatedEvent(Guid UserId, string UserName) : IEvent;

// Commands can return nothing (void) or a typed result
public record SubmitOrderCommand(Guid OrderId, string CustomerName, int Quantity, string Product) : ICommand;
public record PingCommand(Guid CorrelationId, string Message) : ICommand<string>;
```
 Event Handler Example 

```csharp
public class UserCreatedEventHandler(ILogger<UserCreatedEventHandler> logger) 
    : IEventHandler<UserCreatedEvent>
{
    public void Handle(UserCreatedEvent @event)
    {
        logger.LogInformation($"New user created! ID: {@event.UserId}, Name: {@event.UserName}");
    }
}

```
Command Handler Example (void return) 

```csharp
public class SubmitOrderCommandHandler(ILogger<SubmitOrderCommandHandler> logger) 
    : ICommandHandler<SubmitOrderCommand>
{
    public async Task Handle(SubmitOrderCommand command)
    {
        logger.LogInformation($"Processing order {command.OrderId} for {command.CustomerName}: {command.Quantity} x {command.Product}");
        return Task.CompletedTask;        
    }
}

```
 Command Handler Example (typed return) 

```csharp
public class PongCommandHandler(ILogger<PongCommandHandler> logger) 
    : ICommandHandler<PingCommand, string>
{
    public async Task<string> Handle(PingCommand command)
    {
        logger.LogInformation($"Ping received [{command.CorrelationId}] -> {command.Message}");
        return "Pong";  // Responds with a string
    }
}

```

## 🤝 Contributing

Contributions are welcome! 🎉  
If you'd like to contribute, please open an issue first to discuss proposed changes.

