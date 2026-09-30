# Example project

To help you get started with using BetterStack in your .NET projects, we have prepared a simple program that showcases the usage of BetterStack logger.

## Download and install the example project

You can download the example project from GitHub directly or you can clone it to a select directory.

## Run the example project using Visual Studio

Replace `<source_token>` and `<ingesting_host>` with your actual source token and ingesting host in the `nlog.config` file. You can find both values by going to [Better Stack Telemetry](https://telemetry.betterstack.com/dashboard) -> Sources -> Configure.

Open the `.csproj` file in the Visual Studio. Then click on the green play button `ExampleProject` or press **F5** to run the application.

You should see the following output:

```powershell
All done! Now, you can check Better Stack to see your logs
```

## Run in the command line

Replace `<source_token>` and `<ingesting_host>` with your actual source token and ingesting host in the `nlog.config` file. You can find both values by going to [Better Stack Telemetry](https://telemetry.betterstack.com/dashboard) -> Sources -> Configure.


Open the command line in the projects directory and enter the following command:

```powershell
dotnet run
```

You should see the following output:

```powershell
All done! Now, you can check Better Stack to see your logs
```

# Setup

This part shows and explains the usage of the `BetterStack.Logs.NLog` package for .NET as shown in the example application

## Create NLog config

In the root directory of the project, create the `nlog.config` file or copy the file from the example project.
In Visual Studio, you can press **Ctrl + Shift + A** and enter the file name.
This file is used to configure NLog using XML syntax. The content of the file should look like this:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<nlog xmlns="http://www.nlog-project.org/schemas/NLog.xsd"
        xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
        autoReload="true"
        internalLogLevel="Warn"
        internalLogFile="internal.txt">
	<extensions>
		<add assembly="BetterStack.Logs.NLog" />
	</extensions>

	<targets>
    <!-- Dont forget to change <source_token> and <ingesting_host> to your actual source token and ingesting host-->
		<target xsi:type="BetterStack.Logs" name="mybetterstack" layout="${message}" sourceToken="<source_token>" endpoint="<ingesting_host>" />
	</targets>

	<rules>
		<logger name="*" minlevel="Trace" writeTo="mybetterstack" />
	</rules>
</nlog>
```

Make sure that you replace `<source_token>` and `<ingesting_host>` with the actual source token and ingesting host that you can find in the Source settings.

Also, **make sure that the** `nlog.config` **file is set to be copied to the output directory** when running the application. 

If you are using Visual Studio, you can set this option by right-clicking on the file and selecting **Properties.** Find the **Copy to Output Directory** option and set it to **Copy Always.**

Another way is to open the `.csproj` file and add the following directive:

```xml
<ItemGroup>
    <None Update="nlog.config">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
</ItemGroup>
```

## Create logger

First, include the `NLog` library upon which the Better Stack package was built.
Then create a `Logger` instance which will be later used for sending log messages.
To create a `Logger` instance, call `LogManager.GetCurrentClassLogger()` constructor. 

```csharp
using NLog;

// Create logger for current class
var logger = LogManager.GetCurrentClassLogger();
```

This will create a logger for the current class.
In this case, it will be created for the `Program` class and it will add `"logger"` with the value `"Program"` to the context of the JSON log message.

### Colored property values

If you'd like your logged properties to be colored by their type, include following configuration when you create a logger:

```csharp
// Configure NLog to color properties based on their type
NLog.LogManager.Setup().SetupSerialization(
    setupBuilder => setupBuilder.RegisterValueFormatter(new BetterStack.Logs.NLog.ColorValueFormatter())
);
```

### Filter logs

The name of the logger is sent with every log message, in `context.logger`.
This provides an option to filter logs based on the logger that sends them. You can create a logger for each of the logical components of your application and then filter the logs based on the names of the components. 

For example, if you create a logger as a field of the `ShoppingCart` class, the value of `context.logger` will be `ShoppingCart`:

```csharp
public class ShoppingCart
{
     private static Logger ShoppingCartLogger = LogManager.GetCurrentClassLogger();
     //...
}
```

The output will look similar to this:

```json
{
   "dt":"2026-09-30T15:14:25.854796+00:00",
   "message":"Error !!!!!",
   "level":"Error",
   "context":{
      "logger":"ShoppingCart",
      "properties":{},
      "runtime":{
         "class":"ShoppingCart",
         "member":".ctor",
         "file":"C:\\Users\\someuser\\source\\repos\\ExampleProject\\ExampleProject\\ShoppingCart.cs",
         "line":9
      }
   }
}
```

Then it is possible to filter the logs using the following search formula:

```json
context.logger="ShoppingCart"
```

This will only show logs that were sent by the `ShoppingCart` logger.

# Logging

The `Logger` instance we created in the setup is used to send log messages to Better Stack.
It provides 6 logging methods for the 6 default log levels. The log levels and their method are:

- **TRACE** - Trace the code using the `Trace()` method
- **DEBUG** - Send debug messages using the `Debug()` method
- **INFO** - Send informative messages about the application progress using the `Info()` method
- **WARN** - Report non-critical issues using the `Warn()` method
- **ERROR** - Send messages about serious problems using the `Error()` method
- **FATAL** - Report fatal errors that caused the application to crash using the `Fatal()` method

## Logging example

To send a log message of select log level, use the corresponding method. In this example, we will send the **DEBUG** level log and **ERROR** level log.

```csharp
//Send debug messages using the Debug() method
logger.Debug("Debugging is hard, but can be easier with Better Stack!");

//Send message about serious problems using the Error() method
logger.Error("Error occurred! And it's not good.");
```

This will create the following JSON output:

```json
{
   "dt":"2026-09-30T15:14:25.852543+00:00",
   "message":"Debugging is hard, but can be easier with Better Stack!",
   "level":"Debug",
   "context":{
      "logger":"Program",
      "properties":{},
      "runtime":{
         "class":"Program",
         "member":"<Main>$",
         "file":"C:\\Users\\someuser\\source\\repos\\ExampleProject\\ExampleProject\\Program.cs",
         "line":25
      }
   }
}

{
   "dt":"2026-09-30T15:14:25.854796+00:00",
   "message":"Error occurred! And it's not good.",
   "level":"Error",
   "context":{
      "logger":"Program",
      "properties":{},
      "runtime":{
         "class":"Program",
         "member":"<Main>$",
         "file":"C:\\Users\\someuser\\source\\repos\\ExampleProject\\ExampleProject\\Program.cs",
         "line":36
      }
   }
}
```

To send an exception along with the log message, pass it as the first argument:

```csharp
try
{
    throw new InvalidOperationException("Payment gateway timed out");
}
catch (Exception ex)
{
    logger.Error(ex, "Order {orderId} failed", 75423);
}
```

The exception, with its stack trace, is sent in the top-level `exception` field:

```json
{
   "dt":"2026-09-30T15:14:25.854796+00:00",
   "message":"Order 75423 failed",
   "level":"Error",
   "exception":"System.InvalidOperationException: Payment gateway timed out\r\n   at Program.<Main>$(String[] args) in C:\\Users\\someuser\\source\\repos\\ExampleProject\\ExampleProject\\Program.cs:line 43",
   "context":{
      "logger":"Program",
      "properties":{
         "orderId":75423
      },
      "runtime":{
         "class":"Program",
         "member":"<Main>$",
         "file":"C:\\Users\\someuser\\source\\repos\\ExampleProject\\ExampleProject\\Program.cs",
         "line":43
      }
   }
}
```

## Additional configuration

The BetterStack.Logs target will send you logs periodically in batches to optimize network traffic with several retries in case of unexpected HTTP errors.
You can adjust this behavior by setting the `maxBatchSize`, `flushPeriodMilliseconds`, and `retries` parameters to your custom values in your config.

While the logs wait to be sent, the target keeps at most `maxQueueSize` of them in memory, 100000 by default. When the endpoint cannot be reached for a while and the queue fills up, new logs are dropped and an error is written to NLog's internal log.

```xml
<target
   xsi:type="BetterStack.Logs"
   name="mybetterstack"
   layout="${message}"
   sourceToken="<source_token>"
   endpoint="<ingesting_host>"
   maxBatchSize="200"
   maxQueueSize="10000"
   flushPeriodMilliseconds="1000"
   retries="3" />
```

## Structuring the logs

All of the properties that you pass to the log will be stored in a structured form in the `context` section of the logged event.

```csharp
logger.Info("User {user} - {userID} just ordered item {item}", "Josh", 95845, 75423);
```

Code above will create the following output:

```json
{
   "dt":"2026-09-30T15:14:25.852543+00:00",
   "message":"User \"Josh\" - 95845 just ordered item 75423",
   "level":"Info",
   "context":{
      "logger":"Program",
      "properties":{
         "user":"Josh",
         "userID":95845,
         "item":75423
      },
      "runtime":{
         "class":"Program",
         "member":"<Main>$",
         "file":"C:\\Users\\someuser\\source\\repos\\ExampleProject\\ExampleProject\\Program.cs",
         "line":30
      }
   }
}
```

The `properties` field of the `context` contains the arguments that were passed and their values.

## Adding context to all logs

Properties pushed to NLog's `ScopeContext` are sent with every log written inside the scope, once you turn them on with `includeScopeProperties="true"`:

```xml
<target
   xsi:type="BetterStack.Logs"
   name="mybetterstack"
   layout="${message}"
   sourceToken="<source_token>"
   endpoint="<ingesting_host>"
   includeScopeProperties="true" />
```

```csharp
using (ScopeContext.PushProperty("requestId", "0HN4M1"))
{
    logger.Info("User {user} signed in", "Josh");
}
```

Both `requestId` and `user` end up in `context.properties`. On NLog 4, which has no `ScopeContext`, use `includeMdlc="true"` with `MappedDiagnosticsLogicalContext` instead.

To attach a fixed property to every log, add a `<contextproperty>` to the target:

```xml
<target xsi:type="BetterStack.Logs" name="mybetterstack" layout="${message}" sourceToken="<source_token>" endpoint="<ingesting_host>">
   <contextproperty name="service" layout="checkout" />
</target>
```
