import { dotnet } from './_framework/dotnet.js'

const out = document.getElementById('out');
const append = line => { out.textContent += line + '\n'; };
append(`crossOriginIsolated=${self.crossOriginIsolated} SharedArrayBuffer=${typeof SharedArrayBuffer !== 'undefined'} hardwareConcurrency=${navigator.hardwareConcurrency}`);

const { setModuleImports, runMain } = await dotnet.create();
setModuleImports('main.js', { dom: { appendLine: append } });
await runMain();
