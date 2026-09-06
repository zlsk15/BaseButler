using System;
using System.IO;
using System.Text;
using BepInEx.Logging;

namespace CookingSourceExpand
{
    /// <summary>
    /// 自修复前端打补丁：mod 每次启动时，自动往烹饪/手工/交易面板的 HTML 里注入
    /// 「拖动滑动 + 可见滚动条」脚本。
    ///
    /// 为什么改成改文件而不是运行时注入：
    ///   实测确认游戏的 WebView 是代码动态创建的普通对象（非场景组件），FindObjectsOfType
    ///   三路都找不到，运行时注入走不通。改为直接给 StreamingAssets 下的 HTML 打补丁，
    ///   但用"哨兵标记"保证幂等（只打一次），并且每次启动都检查——若游戏 Steam 更新把
    ///   HTML 覆盖回原样，下次启动 mod 会重新打上，从而"自修复"，抗更新。
    ///   首次打补丁前会先备份原文件为 .cse.bak，便于回滚。
    /// </summary>
    internal static class WebViewHtmlPatcher
    {
        private const string Sentinel = "CSE_AUTOPATCH_DRAG";
        private static string UiRoot
        {
            get
            {
                try
                {
                    var dir = Path.GetDirectoryName(typeof(WebViewHtmlPatcher).Assembly.Location);
                    if (string.IsNullOrEmpty(dir)) return "";
                    var gameRoot = Path.GetFullPath(Path.Combine(dir, "..", ".."));
                    var ui = Path.Combine(gameRoot, "SurvivalLog_Data", "StreamingAssets", "WebUI", "UI");
                    return Directory.Exists(ui) ? ui : "";
                }
                catch (Exception) { return ""; }
            }
        }

        private static readonly string[] Targets = { "Cooking", "ToolTable", "TradeUI" };

        private const string Css =
            "<style id=\"cse-scrollbar\" data-cse=\"1\">" +
            ".bag-tabs-scroll,.left-tabs-scroll{overflow-x:auto!important;scrollbar-width:thin!important;scrollbar-color:rgba(251,176,52,.75) rgba(255,255,255,.08)!important;}" +
            ".bag-tabs-scroll::-webkit-scrollbar,.left-tabs-scroll::-webkit-scrollbar{display:block!important;height:8px!important;background:rgba(255,255,255,.08)!important;}" +
            ".bag-tabs-scroll::-webkit-scrollbar-thumb,.left-tabs-scroll::-webkit-scrollbar-thumb{background:rgba(251,176,52,.75)!important;border-radius:4px!important;}" +
            ".bag-tabs-scroll::-webkit-scrollbar-track,.left-tabs-scroll::-webkit-scrollbar-track{background:rgba(255,255,255,.05)!important;}" +
            "</style>";

        private const string JsHead = "<script>";
        private const string JsBody =
            "(function(){var SEL='#bagTabsScroll, #leftTabsScroll, .bag-tabs-scroll, .left-tabs-scroll';" +
            "function attach(s){if(s.__cseD)return;s.__cseD=1;var d=0,x=0,l=0;" +
            "function down(e){d=1;x=e.clientX;l=s.scrollLeft;s.style.cursor='grabbing';e.preventDefault();}" +
            "function move(e){if(!d)return;s.scrollLeft=l-(e.clientX-x);e.preventDefault();}" +
            "function up(){d=0;s.style.cursor='';}" +
            "s.addEventListener('pointerdown',down,{passive:false});window.addEventListener('pointermove',move,{passive:false});" +
            "window.addEventListener('pointerup',up);s.addEventListener('pointerleave',up);}" +
            "function go(){document.querySelectorAll(SEL).forEach(attach);}" +
            "function loop(){var n=0;var t=setInterval(function(){go();if(++n>200)clearInterval(t);},300);}" +
            "if(document.readyState!=='loading')loop();else document.addEventListener('DOMContentLoaded',loop);})();";
        private const string JsTail = "</script>";
        private const string SentinelComment = "<!-- " + Sentinel + " -->";

        /// <summary>「点击配方即补齐并制作」功能哨兵，仅 ToolTable 打，独立于上面的拖动滑动哨兵。
        /// 去掉所有显式按钮，改为包裹 core.UnitySendEvent 拦截官方 RECIPE_CLICK：玩家点选配方 → 脚本捕获 recipeKey
        /// (且官方据此刷新每个柜子的 shortCount 缺口角标) → 延迟触发 gather，只扫短缺的柜子整体搬料到工作台、
        /// 带重试实盘校验、最后内部 RECIPE_CLICK 重新锁定 + CRAFT，确保自动制作做的必然是该配方，不会混做出别的。
        /// 取料柜：只扫官方标了 shortCount>0（缺口角标）的柜；若官方没标任何缺口柜，说明官方自己判定材料缺失，直接取消制作，
        /// 不做全扫回退（与官方判定保持一致）。
        /// 内部重锁配方时用 __cseInternalClick 标志防递归，避免误触发 gather。</summary>
        private const string GatherSentinel = "CSE_AUTOPATCH_GATHER";
        private const string GatherJs =
            "<script>" +
            "(function(){" +
            "if(window.__cseGatherLoaded)return;window.__cseGatherLoaded=1;" +
            "function dbg(s){}" +
"function toast(msg,ok){}" +
            "window.__cseWait=[];function notifyBag(d){var w=window.__cseWait;for(var i=w.length-1;i>=0;i--){var x=w[i];if(x.done)continue;var ok=false;try{ok=x.pred(d);}catch(e){ok=false;}if(ok){x.done=true;clearTimeout(x.to);w.splice(i,1);x.res(d);}}}" +
            "(function tryApply(){try{var ow=window.applyBagMsg;if(typeof ow==='function'&&!window.__cseWrappedBag){window.__cseWrappedBag=1;" +
            "window.applyBagMsg=function(d){try{notifyBag(d);}catch(e){}window.__cseLastBag=d;window.__cseBagSeq=(window.__cseBagSeq||0)+1;" +
            "if(window.__cseWbO&&d&&d.bagOwnerId===window.__cseWbO)window.__cseWbBag=d;try{ow(d);}catch(e){}};return;}" +
            "}catch(e){}if(!window.__cseWrappedBag)setTimeout(tryApply,150);})();" +
            "try{if(core&&core.UnitySendEvent&&!window.__cseWrappedUE){window.__cseWrappedUE=1;var ue=core.UnitySendEvent;" +
            "core.UnitySendEvent=function(ev,data){" +
            "var ret=ue.apply(this,arguments);" +
            "if(ev==='RECIPE_CLICK'&&data&&data.recipeKey&&!window.__cseInternalClick){" +
            "var r=(typeof recipeByKey!=='undefined'&&recipeByKey)?recipeByKey[data.recipeKey]:null;" +
            "if(r){window.__cseRecipe=r;var _st=Date.now();" +
            "setTimeout(async function(){var res='wait',_dl=_st+2800;while(Date.now()<_dl&&res==='wait'){res=await gather(r.recipeKey,_st);if(res==='wait')await sleep(220);}},30);}" +
            "}" +
            "return ret;};}}catch(e){}" +
            "function waitBag(pred,ms){return new Promise(function(res,rej){var to=setTimeout(function(){if(x.done)return;x.done=true;var i=window.__cseWait.indexOf(x);if(i>=0)window.__cseWait.splice(i,1);rej(new Error('wait-timeout'));},ms||1500);var x={pred:pred,to:to,res:res,done:false};window.__cseWait.push(x);});}" +
            "function sleep(ms){return new Promise(function(r){setTimeout(r,ms);});}" +
            "function getMats(){var r=window.__cseRecipe;if(!r)return null;var a=[];if(r.materialsJson){try{a=JSON.parse(r.materialsJson);}catch(e){}}return a.filter(function(m){return m&&m.need>0&&m.name;});}" +
            "function wbItems(){return (typeof workbench!=='undefined'&&workbench&&workbench.getItems)?workbench.getItems():[];}" +
            "function wbInventory(){var m={};(wbItems()||[]).forEach(function(o){if(!o)return;if(o.name)m[o.name]=(m[o.name]||0)+(o.count||1);if(o.itemId!=null)m['#id'+(parseInt(o.itemId))]=(m['#id'+(parseInt(o.itemId))]||0)+(o.count||1);});return m;}" +
            "function wbOcc(){var occ={};(wbItems()||[]).forEach(function(o){if(!o)return;for(var i=0;i<(o.w||1);i++)for(var j=0;j<(o.h||1);j++)occ[(o.x+i)+','+(o.y+j)]=1;});return occ;}" +
            "function nextCell(){var lm=(typeof lastBagMsg!=='undefined'?lastBagMsg:{})||{};var cols=lm.workbenchCols||6,rows=lm.workbenchRows||5;var occ=window.__cseOcc||wbOcc();" +
            "for(var y=0;y<rows;y++)for(var x=0;x<cols;x++){var k=x+','+y;if(!occ[k]){occ[k]=1;return{x:x,y:y};}}return null;}" +
            "function wbOwner(){try{if(workbench&&workbench.getOwnerId)return workbench.getOwnerId();}catch(e){}return 0;}" +
            "function norm(s){return String(s||'').replace(/[\\s\\u3000]/g,'');}" +
            "function nameHit(a,b){var x=norm(a),y=norm(b);if(!x||!y)return false;return x===y;}" +
            "function fillFromCabinet(target,d,expect,order){" +
            "var items=(d&&d.bagItems)||[];var sent=0;var _wb=wbOwner();var sameOwner=(_wb!==0&&target===_wb);" +
            "for(var oi=0;oi<order.length;oi++){var key=order[oi];var e=expect[key];var needN=Math.max(0,e.need-e.have);if(needN<=0)continue;var kn=norm(key);var wantId=e.itemId;" +
            "for(var ii=0;ii<items.length&&needN>0;ii++){var it=items[ii];if(!it||(it.count||0)<1)continue;var idm=(wantId!=null&&((it.itemId!=null&&parseInt(it.itemId)===parseInt(wantId))||(it.configId!=null&&parseInt(it.configId)===parseInt(wantId))));var hmm=nameHit(it.name,key);if(!idm&&!hmm)continue;" +
            "if(sameOwner){e.have+=Math.min(needN,it.count);sent++;needN=Math.max(0,e.need-e.have);continue;}" +
            "var cell=nextCell();if(!cell){toast('工作台没有空格',false);return -1;}" +
            "core.UnitySendEvent('ITEM_MOVE',{itemId:parseInt(it.itemId),fromOwnerId:target,toOwnerId:_wb,x:cell.x,y:cell.y});" +
            "dbg('搬 '+(it.name||key)+' itemId'+it.itemId+' '+target+'→'+_wb+'@'+cell.x+','+cell.y+' 堆'+it.count);" +
            "e.have+=Math.min(needN,it.count);sent++;needN=Math.max(0,e.need-e.have);}}return sent;}" +
            "function itemNames(d){var o={};(d&&d.bagItems||[]).forEach(function(x){if(x&&x.name){var k=x.name+'#'+(x.itemId||0);o[k]=(x.count||1);}});var a=[];for(var k in o)a.push(k.replace('#', ' x')+'='+o[k]);return a.join(', ');}" +
            "function countByName(list){var m={};(list||[]).forEach(function(o){if(o&&o.name)m[o.name]=(m[o.name]||0)+(o.count||1);});return m;}" +
            "async function scanCabs(list,expect,order,tag,ms){" +
"if(ms==null)ms=1200;" +
"for(var ci=0;ci<list.length&&order.length;ci++){var t=list[ci];var target=t.ownerId;" +
"for(var rtry=0;rtry<2;rtry++){dbg((tag||'扫')+' 柜'+ci+' owner'+target+((rtry>0)?' 重试'+rtry:''));" +
"var prom=waitBag(function(x){return x&&x.bagOwnerId===target;},ms);" +
"try{core.UnitySendEvent('TAB_SWITCH',{tab:t.idx});}catch(e){}" +
"var d;try{d=await prom;}catch(e){dbg('  等柜超时');if(rtry===0)continue;try{window.__cseTimeoutOwn[target]=t;}catch(_){}break;}" +
"try{if(d)window.__cseScannedOwn[target]=1;}catch(e){}" +
"dbg('  收到 owner'+d.bagOwnerId+' 物品'+(d.bagItems||[]).length+' ['+itemNames(d)+']');" +
            "dbg('  含缺料: '+order.map(function(k){var c=(d.bagItems||[]).reduce(function(a,it){return a+((it&&it.name&&nameHit(it.name,k))?(it.count||1):0);},0);return c>0?k+'x'+c:null;}).filter(Boolean).join(',')||'无');" +
"var sent=fillFromCabinet(target,d,expect,order);if(sent<0)return -1;" +
"order=order.filter(function(k){return expect[k].have<expect[k].need;});" +
"try{if(!sent&&order.length&&(d.bagItems||[]).length>0)window.__cseEmptyOwn[target]=t;}catch(e){}" +
"dbg('  本柜搬'+sent+' 仍缺['+order.join('|')+'] 期望['+order.map(function(k){return expect[k].name;}).join(',')+']');break;}}return 0;}" +
            "function doCraft(){" +
            "return new Promise(function(res){var r=window.__cseRecipe;if(!r||!r.recipeKey){toast('未获得选中配方，请先点选配方',false);return res();}" +
            "setTimeout(function(){window.__cseInternalClick=1;try{core.UnitySendEvent('RECIPE_CLICK',{recipeKey:r.recipeKey});}catch(e){}window.__cseInternalClick=0;" +
            "setTimeout(function(){try{core.UnitySendEvent('CRAFT');}catch(e){}res();},90);},400);});}" +
            "async function verifyMats(expect){" +
            "var deadline=Date.now()+3500;" +
            "function calc(src){var bid={};var hits=[];(src||[]).forEach(function(o){if(!o)return;if(o.itemId!=null){bid[''+(parseInt(o.itemId))]=(bid[''+(parseInt(o.itemId))]||0)+(o.count||1);}hits.push(o);});var idGiven={};var out=[];for(var k in expect){var e=expect[k];var haveN=0;if(e.itemId!=null){haveN+=bid[''+(parseInt(e.itemId))]||0;idGiven[k]=1;}for(var h=0;h<hits.length;h++){var it=hits[h];if(!it)continue;if(e.itemId!=null&&it.itemId!=null&&parseInt(it.itemId)===parseInt(e.itemId))continue;if(!idGiven[k]&&nameHit(it.name,k))haveN+=it.count||1;else if(idGiven[k]&&nameHit(it.name,k))haveN+=it.count||1;}if(haveN<e.need)out.push(e.name);}return out;}" +
            "function wbBag(){return (typeof window.__cseWbBag!=='undefined'&&window.__cseWbBag&&window.__cseWbBag.bagItems)?window.__cseWbBag.bagItems:null;}" +
            "while(Date.now()<deadline){" +
            "var f=wbItems(),m0=calc(f);if(!m0.length)return m0;" +
            "var bs=wbBag();var m1=bs?calc(bs):null;if(m1&&!m1.length)return [];" +
            "dbg('仍缺['+m0.join('|')+'] 后台袋['+(bs?calc(bs).join('|'):'无')+']');" +
            "await sleep(300);}" +
            "var bs=wbBag();var m1=bs?calc(bs):null;if(m1&&!m1.length)return [];" +
            "return calc(wbItems());}" +
            "async function ensureRecipeSelected(key){" +
            "if(!key)return;var before=window.__cseBagSeq||0;" +
            "window.__cseInternalClick=1;try{core.UnitySendEvent('RECIPE_CLICK',{recipeKey:key});}catch(e){}window.__cseInternalClick=0;" +
            "var deadline=Date.now()+1600;while(Date.now()<deadline&&((window.__cseBagSeq||0)<before+1)){await sleep(40);}}" +
            "async function gather(key,start){" +
            "if(window.__cseBusy)return 'wait';window.__cseBusy=1;var _st=start||Date.now();" +
            "try{" +
            "if(key)await ensureRecipeSelected(key);" +
            "var r=window.__cseRecipe;if(!r||!r.recipeKey){await sleep(120);return 'wait';}" +
            "var mats=getMats();if(!mats||!mats.length){dbg('配方材料未就绪，等待…');return 'wait';}" +
            "try{dbg('配方材料: '+mats.map(function(m){return (m.name||'?')+'(need'+(m.need||0)+')'+(m.itemId?('/itemId'+m.itemId):'')+(m.configId?('/cfg'+m.configId):'');}).join(' | ')+' || keys:'+Object.keys(mats[0]||{}).join(','));}catch(e){}" +
            "var expect={},order=[];mats.forEach(function(m){var k=m.name;if(!(k in expect)){expect[k]={name:k,need:m.need||0,have:0,itemId:m.itemId||m.configId||null};order.push(k);}});" +
            "var winv=wbInventory();order.forEach(function(k){var e=expect[k];var haveN=winv[k]||0;if(e.itemId!=null)haveN+=winv['#id'+(parseInt(e.itemId))]||0;if(haveN)e.have=Math.max(e.have,haveN);});" +
            "order=order.filter(function(k){return expect[k].have<expect[k].need;});" +
            "dbg('配方['+(r.name||'')+'] 缺['+order.map(function(k){return expect[k].name+'('+expect[k].need+')';}).join('|')+'] 台上['+JSON.stringify(winv)+']');" +
            "if(!order.length){toast('材料已备齐，自动制作…',true);await doCraft();return 'ok';}" +
            "var lm=(typeof lastBagMsg!=='undefined'?lastBagMsg:{})||{};var cabs=lm.cabinetTabs||[];var drawerO=lm.drawerOwnerId||0;var drawerShort=(lm.drawerShortCount||0);" +
            "if(!wbOwner()){return 'wait';}" +
            "function bpOwn(){try{return backpack&&backpack.getOwnerId?backpack.getOwnerId():0;}catch(e){return 0;}}" +
            "function fillCurrent(){if(!order.length)return 0;var cd=(typeof window.__cseLastBag!=='undefined'&&window.__cseLastBag)?window.__cseLastBag:(typeof lastBagMsg!=='undefined'?lastBagMsg:null);" +
            "if(!cd||!cd.bagItems)return 0;var oc=cd.bagOwnerId||0;if(!oc||oc===wbOwner())return 0;" +
            "var ok=(oc===bpOwn())?1:0;for(var q=0;q<cabs.length;q++){if(cabs[q]&&cabs[q].ownerId===oc)ok=1;}if(oc===drawerO)ok=1;if(!ok)return 0;" +
            "window.__cseOcc=wbOcc();var s=fillFromCabinet(oc,cd,expect,order);" +
            "order=order.filter(function(k){return expect[k].have<expect[k].need;});" +
            "dbg('直接用已展示面板(owner'+oc+')搬'+s+' 仍缺['+order.join('|')+']');return s;}" +
            "function allSrcs(){var _wb=wbOwner(),a=[],pp=bpOwn();if(pp)a.push({ownerId:pp,idx:0});if(drawerO&&drawerO!==_wb)a.push({ownerId:drawerO,idx:1});for(var i=0;i<cabs.length;i++){var c=cabs[i];if(c&&c.ownerId&&c.ownerId!==_wb)a.push({ownerId:c.ownerId,idx:2+i});}return a;}" +
            "window.__cseEmptyOwn=window.__cseEmptyOwn||{};fillCurrent();" +
            "if(order.length){" +
            "var scan=[];if(drawerO&&drawerShort>0)scan.push({ownerId:drawerO,idx:1});for(var i=0;i<cabs.length;i++){var c=cabs[i];if(c&&c.ownerId&&(c.shortCount||0)>0)scan.push({ownerId:c.ownerId,idx:2+i});}" +
"if(scan.length){window.__cseOcc=wbOcc();window.__cseLastBag=null;window.__cseScannedOwn={};window.__cseTimeoutOwn={};window.__cseWbO=wbOwner();window.__cseWbBag=null;" +
            "dbg('官方标缺口柜 '+scan.length+' 个，工作台owner'+wbOwner());toast('正在从缺口柜补齐材料…',true);" +
            "if(await scanCabs(scan,expect,order,'缺')<0)return 'ok';" +
            "window.__cseOcc=wbOcc();window.__cseLastBag=null;}}else{toast('材料已备齐，自动制作…',true);}" +
            "if(order.length){var full=allSrcs().filter(function(x){return !(window.__cseScannedOwn||{})[x.ownerId];});" +
            "dbg('全扫源 '+full.length+'/总'+allSrcs().length+' 柜tab'+cabs.length+' 背包o'+ ((function(){try{return backpack.getOwnerId?backpack.getOwnerId():0;}catch(e){return -1;}})()) +' 工作台o'+wbOwner() );" +
            "dbg('源厂家 ['+full.map(function(x){return 'o'+x.ownerId;}).join(',')+']');" +
            "if(full.length){dbg('仍缺['+order.join('|')+']，全扫(含背包)补齐');toast('储物柜/背包补齐…',true);" +
"window.__cseOcc=wbOcc();window.__cseLastBag=null;if(await scanCabs(full,expect,order,'全')<0)return 'ok';}}" +
"if(order.length){var to=[];for(var _oo in (window.__cseTimeoutOwn||{}))to.push(window.__cseTimeoutOwn[_oo]);for(var _ee in (window.__cseEmptyOwn||{}))to.push(window.__cseEmptyOwn[_ee]);" +
"if(to.length){dbg('慢速重扫 '+to.length+' 个柜(超时+有料未命中)');toast('重扫有料柜…',true);" +
"window.__cseOcc=wbOcc();window.__cseLastBag=null;if(await scanCabs(to,expect,order,'重',3200)<0)return 'ok';}}" +
            "if(order.length){var any=order.some(function(k){return expect[k].have>0;});if(any&&Date.now()-_st<2500){dbg('部分搬到['+order.join('|')+']，窗口内重扫补齐');return 'wait';}" +
            "if(!any){dbg('一处都没搬到['+order.join('|')+']，取消制作');toast('各处都找不到：'+order.join('-')+'，已取消制作',false);return 'cancel';}}" +
            "await sleep(900);" +
            "dbg('期望明细['+Object.keys(expect).map(function(k){return expect[k].name+' hv'+expect[k].have+'/'+expect[k].need;}).join(' | ')+']');" +
            "var missA=order.filter(function(k){return expect[k].have<expect[k].need;});" +
            "if(missA.length){dbg('材料不足['+missA.join('|')+']，取消制作，不发CRAFT避免误做其他配方');toast('材料不足，已取消制作：'+missA.join('-'),false);return 'cancel';}" +
            "dbg('材料已备齐(按实际搬运累计)，进入自动制作，交由官方后端最终判定');" +
            "toast('自动制作中…',true);" +
            "await doCraft();" +
            "dbg('已发送制作');" +
            "return 'ok';" +
            "}catch(e){dbg('异常 '+e.message);try{toast('补齐出错 '+e.message,false);}catch(_){}}" +
            "finally{window.__cseBusy=0;}return 'cancel';}" +
            "})();" +
            "</script>";

        internal static void ApplyAll(ManualLogSource log)
        {
            try
            {
                if (string.IsNullOrEmpty(UiRoot) || !Directory.Exists(UiRoot))
                {
                    log.LogWarning($"[CookingSourceExpand] 未定位到 WebUI 目录（{UiRoot}），前端补丁跳过。");
                    return;
                }
                int patched = 0, skipped = 0, failed = 0;
                foreach (var name in Targets)
                {
                    var path = Path.Combine(UiRoot, name, name + ".html");
                    if (!File.Exists(path)) { skipped++; continue; }
                    try
                    {
                        if (PatchFile(path, log)) patched++; else skipped++;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        log.LogWarning($"[CookingSourceExpand] 补丁 {name} 失败：{e.Message}");
                    }
                }
                log.LogInfo($"[CookingSourceExpand] 前端『拖动滑动+滚动条』补丁：新打 {patched} 个，已存在跳过 {skipped} 个，失败 {failed} 个。");
                ApplyGather(log);
            }
            catch (Exception e)
            {
                log.LogWarning($"[CookingSourceExpand] 前端打补丁异常：{e.Message}");
            }
        }

        private static bool PatchFile(string path, ManualLogSource log)
        {
            string text;
            try { text = File.ReadAllText(path, new UTF8Encoding(false)); }
            catch (Exception e) { log.LogWarning($"[CookingSourceExpand] 读取 {path} 失败：{e.Message}"); return false; }

            if (text.Contains(Sentinel))
            {
                return false; // 已打过，跳过
            }

            // 首次打：备份原文件
            var bak = path + ".cse.bak";
            if (!File.Exists(bak))
            {
                try { File.WriteAllText(bak, text, new UTF8Encoding(false)); }
                catch (Exception e) { log.LogWarning($"[CookingSourceExpand] 备份 {path} 失败：{e.Message}"); }
            }

            // 注入 CSS 到 <head>
            int headEnd = text.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
            if (headEnd >= 0)
            {
                int gt = text.IndexOf('>', headEnd);
                if (gt >= 0)
                {
                    text = text.Substring(0, gt + 1) + "\n" + Css + "\n" + text.Substring(gt + 1);
                }
            }

            // 注入 JS 到 </body> 前
            int bodyIdx = text.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyIdx >= 0)
            {
                text = text.Substring(0, bodyIdx) + JsHead + JsBody + JsTail + "\n" + SentinelComment + "\n" + text.Substring(bodyIdx);
            }
            else
            {
                text = text + "\n" + SentinelComment;
            }

            File.WriteAllText(path, text, new UTF8Encoding(false));
            log.LogInfo($"[CookingSourceExpand] 已给 {Path.GetFileName(path)} 打上『拖动滑动+滚动条』补丁。");
            return true;
        }

        /// <summary>仅对工作台面板注入「补齐材料」按钮脚本（独立哨兵，幂等，抗更新）。只搬料、不自动制作。</summary>
        private static void ApplyGather(ManualLogSource log)
        {
            try
            {
                var path = Path.Combine(UiRoot, "ToolTable", "ToolTable.html");
                if (!File.Exists(path))
                {
                    log.LogWarning("[CookingSourceExpand] ToolTable.html 不存在，「点击配方补齐并制作」补丁跳过。");
                    return;
                }
                string text;
                try { text = File.ReadAllText(path, new UTF8Encoding(false)); }
                catch (Exception e) { log.LogWarning($"[CookingSourceExpand] 读取 ToolTable.html 失败：{e.Message}"); return; }

                if (text.Contains(GatherSentinel)) return; // 已打过，跳过

                int bodyIdx = text.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (bodyIdx >= 0)
                {
                    text = text.Substring(0, bodyIdx) + GatherJs + "\n<!-- " + GatherSentinel + " -->\n" + text.Substring(bodyIdx);
                }
                else
                {
                    text = text + "\n<!-- " + GatherSentinel + " -->";
                }
                File.WriteAllText(path, text, new UTF8Encoding(false));
                log.LogInfo("[CookingSourceExpand] 已给 ToolTable.html 打上『点击配方补齐并制作』补丁。");
            }
            catch (Exception e)
            {
                log.LogWarning($"[CookingSourceExpand] 『点击配方补齐并制作』补丁失败：{e.Message}");
            }
        }
    }
}