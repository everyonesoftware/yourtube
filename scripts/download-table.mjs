export function renderTable(manifest) {
 const base=`https://github.com/everyonesoftware/yourtube/releases/download/${manifest.tag}`;
 return `| Platform | Version | Download | Status |\n| --- | --- | --- | --- |\n| Windows x64 | ${manifest.version} | [Installer](${base}/YourTubeWindows-win-x64-Setup.exe) · [SHA-256](${base}/YourTubeWindows-win-x64-Setup.exe.sha256) | Proof of concept; authenticated account acceptance pending |`;
}
export function replaceTable(text, table) {
 const start='<!-- current-downloads:start -->', end='<!-- current-downloads:end -->';
 if(text.split(start).length!==2 || text.split(end).length!==2 || text.indexOf(end)<text.indexOf(start)) throw new Error('Missing/ambiguous current-downloads markers');
 return text.slice(0,text.indexOf(start)+start.length)+'\n'+table+'\n'+text.slice(text.indexOf(end));
}
