/*
 * FlClash Pilot - 在 FlClash 的“覆写脚本 / 配置脚本”中运行。
 * 此文件不是 APK。由 FlClash / mihomo 在后台执行周期检测，无需另一应用常驻。
 * 加载后，在原来的“手动选择”组内，亲自选择新增的“[Pilot] 自动 · 组名”。
 * 不主动执行切换；加载后请核对客户端保存的选择。
 * 不修改规则、DNS、订阅地址、节点或控制接口。
 * 自动选线依据响应延迟和可用性，不能保证下载带宽最大。
 */
function main(config) {
  var settings = {
    // 为空则为含直接节点 / 代理集合的手动组提供自动选线选项。
    // 如只想管理某组，可写：targetGroups: ["PROXY"]
    targetGroups: [],
    interval: 300,
    tolerance: 80,
    timeout: 5000,
    testUrl: "https://www.gstatic.com/generate_204",
    excludeNames: "剩余|到期|流量|官网|套餐|Traffic|Expire|(?i:reset|remaining)",
    prefix: "[Pilot] 自动 · "
  };
  if (!config || typeof config !== "object" || Array.isArray(config)) {
    throw new Error("需要 FlClash 传入配置对象；不要作为订阅文件导入。");
  }
  var copy = JSON.parse(JSON.stringify(config));
  var groups = copy["proxy-groups"];
  if (!Array.isArray(groups)) throw new Error("配置中没有代理组，请先选择已有的订阅配置。");
  var metadataKey = "x-flclash-pilot";
  var oldMetadata = copy[metadataKey];
  if (oldMetadata && (oldMetadata.version !== 1 || oldMetadata.tool !== "FlClashPilot" || !Array.isArray(oldMetadata.groups))) throw new Error("自动选线标记冲突，已停止处理。");
  var oldNames = oldMetadata && Array.isArray(oldMetadata.groups) ? oldMetadata.groups : [];
  oldNames.forEach(function(name) {
    var previous = groups.filter(function(group) { return group.name === name; });
    if (typeof name !== "string" || name.indexOf(settings.prefix) !== 0 || previous.length !== 1 || previous[0].type !== "url-test") {
      throw new Error("自动选线标记与现有组不一致，已停止处理。");
    }
  });
  // Make reapplying the script deterministic without accumulating groups or references.
  groups = groups.filter(function(group) { return oldNames.indexOf(group.name) === -1; });
  groups.forEach(function(group) {
    if (Array.isArray(group.proxies)) group.proxies = group.proxies.filter(function(name) { return oldNames.indexOf(name) === -1; });
  });
  var groupNames = {};
  groups.forEach(function(group) { groupNames[group.name] = true; });
  var nodeNames = {};
  var blocked = {direct:true,reject:true,rejectdrop:true,pass:true,compatible:true,dns:true};
  (Array.isArray(copy.proxies) ? copy.proxies : []).forEach(function(node) {
    if (node && typeof node.name === "string" && !blocked[String(node.type || "").toLowerCase()]) nodeNames[node.name] = true;
  });
  var providers = copy["proxy-providers"] || {};
  var generated = [];
  var usedProviders = {};
  groups.forEach(function(group) {
    if (group.type !== "select") return;
    if (settings.targetGroups.length && settings.targetGroups.indexOf(group.name) === -1) return;
    var nodes = (Array.isArray(group.proxies) ? group.proxies : []).filter(function(name) { return nodeNames[name] === true; });
    var use = (Array.isArray(group.use) ? group.use : []).filter(function(name) { return Object.prototype.hasOwnProperty.call(providers, name); });
    var allNodes = group["include-all"] === true || group["include-all-proxies"] === true;
    var allProviders = group["include-all"] === true || group["include-all-providers"] === true;
    if (!nodes.length && !use.length && !allNodes && !allProviders) return;
    var name = settings.prefix + group.name;
    if (groupNames[name] || nodeNames[name] || Object.prototype.hasOwnProperty.call(providers, name)) throw new Error("已有同名组或节点：" + name + "；没有更改原配置。");
    var automatic = {
      name:name,
      type:"url-test",
      url:settings.testUrl,
      interval:settings.interval,
      tolerance:settings.tolerance,
      timeout:settings.timeout,
      lazy:true,
      "expected-status":204,
      "max-failed-times":3,
      "empty-fallback":"REJECT",
      "exclude-type":"Direct|Reject|RejectDrop|Pass|Compatible|DNS" + (group["exclude-type"] ? "|" + group["exclude-type"] : "")
    };
    if (nodes.length) automatic.proxies = nodes;
    if (use.length) automatic.use = use;
    ["include-all", "include-all-proxies", "include-all-providers", "filter"].forEach(function(key) {
      if (Object.prototype.hasOwnProperty.call(group,key)) automatic[key] = group[key];
    });
    automatic["exclude-filter"] = group["exclude-filter"] ? "(?:" + group["exclude-filter"] + ")|(?:" + settings.excludeNames + ")" : settings.excludeNames;
    generated.push(automatic);
    group.proxies = (Array.isArray(group.proxies) ? group.proxies : []).concat([name]);
    use.forEach(function(provider) { usedProviders[provider] = true; });
    if (allProviders) Object.keys(providers).forEach(function(provider) { usedProviders[provider] = true; });
  });
  if (!generated.length) throw new Error("未找到含直接节点或代理集合的手动组；没有修改配置。请提供不含凭证的组名与组类型以便适配。");
  // Provider members are checked by their own health-check, rather than a group's proxies check.
  Object.keys(usedProviders).forEach(function(name) {
    var provider = providers[name];
    var check = provider["health-check"];
    if (!check || check.enable !== true) {
      var next = check ? JSON.parse(JSON.stringify(check)) : {};
      next.enable = true;
      next.url = settings.testUrl;
      next.interval = settings.interval;
      next.timeout = settings.timeout;
      next.lazy = true;
      next["expected-status"] = 204;
      provider["health-check"] = next;
    }
  });
  copy["proxy-groups"] = groups.concat(generated);
  copy[metadataKey] = {tool:"FlClashPilot", version:1, groups:generated.map(function(group) { return group.name; })};
  return copy;
}
