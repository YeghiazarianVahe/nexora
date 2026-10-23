import fs from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
const root=path.resolve(process.argv[2]);
const npm=JSON.parse(await fs.readFile(path.join(root,'npm-inventory.json'),'utf8'));
const nuget=JSON.parse((await fs.readFile(path.join(root,'dotnet-inventory.json'),'utf8')).replace(/^\uFEFF/,''));
const packages=new Map();
function add(ecosystem,name,version){
  if(!name||!version)return;
  const key=`${ecosystem}:${name}@${version}`;
  if(packages.has(key))return;
  packages.set(key,{name,versionInfo:version,SPDXID:`SPDXRef-Package-${packages.size}`,downloadLocation:'NOASSERTION',filesAnalyzed:false,licenseConcluded:'NOASSERTION',licenseDeclared:'NOASSERTION',
    externalRefs:[{referenceCategory:'PACKAGE-MANAGER',referenceType:'purl',referenceLocator:`pkg:${ecosystem}/${name.replace('@','%40')}@${version}`}]});
}
function walk(tree){for(const[name,node]of Object.entries(tree.dependencies??{})){add('npm',name,node.version);walk(node);}}
walk(npm);
for(const project of nuget.projects??[])for(const framework of project.frameworks??[])for(const pkg of [...(framework.topLevelPackages??[]),...(framework.transitivePackages??[])])add('nuget',pkg.id,pkg.resolvedVersion);
const bom={spdxVersion:'SPDX-2.3',dataLicense:'CC0-1.0',SPDXID:'SPDXRef-DOCUMENT',name:'Nexora working-tree candidate dependency inventory',documentNamespace:`urn:uuid:${randomUUID()}`,
  creationInfo:{created:new Date().toISOString().replace(/\.\d+Z$/,'Z'),creators:['Tool: Nexora inventory exporter']},
  comment:'Generated from installed npm and resolved NuGet graphs, including development/test dependencies. Licenses, supply-chain attestations and runtime completeness are not certified.',packages:[...packages.values()]};
await fs.writeFile(path.join(root,'sbom.spdx.json'),JSON.stringify(bom,null,2));
console.log(`SPDX inventory: ${packages.size} package/version entries; license assertions require review.`);
