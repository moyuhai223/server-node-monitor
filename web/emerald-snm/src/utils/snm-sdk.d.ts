declare module '*/snm-client.js' {
  export function createClient(options: {theme:string;historyPoints:number}): { state: any; on: (name:string, fn:(...args:any[])=>void)=>void; start:()=>Promise<void>; stop:()=>Promise<void> }
}
