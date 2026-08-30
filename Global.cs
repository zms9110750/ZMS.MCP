// Project-type usings
#if IS_MCP
global using ModelContextProtocol.Server;
global using System.ComponentModel;
#endif
#if USE_DI
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Logging;
#endif
#if IS_TEST
global using Xunit;
#endif
