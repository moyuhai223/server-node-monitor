import * as signalR from '@microsoft/signalr'
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack'

/** Admin hub connection (docs/PROTOCOL.md 6): JWT via accessTokenFactory, MessagePack, custom retry policy. */
export function createAdminHub({ getToken, onSnapshot, onBatch, onAlert, onNodesChanged, onState }) {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/admin', {
      accessTokenFactory: getToken,
      transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling,
    })
    .withHubProtocol(new MessagePackHubProtocol())
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: ctx => [0, 2000, 5000, 10000, 30000][ctx.previousRetryCount] ?? 60000 })
    .configureLogging(signalR.LogLevel.Warning)
    .build()
  connection.serverTimeoutInMilliseconds = 45000
  connection.keepAliveIntervalInMilliseconds = 15000
  connection.on('snapshot', onSnapshot)
  connection.on('batch', onBatch)
  connection.on('alert', onAlert)
  connection.on('nodesChanged', onNodesChanged)
  connection.onreconnecting(() => onState('reconnecting'))
  connection.onreconnected(async () => {
    onState('connected')
    try {
      onSnapshot(await connection.invoke('GetSnapshot'))
    }
    catch (e) {
      console.warn('GetSnapshot after reconnect failed', e)
    }
  })
  connection.onclose(() => onState('disconnected'))
  return connection
}
