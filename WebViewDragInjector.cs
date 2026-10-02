using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;

namespace BaseButler.SourceExpand
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

        // ★方案A吸收（参考 SLCookLink "stable-order v1"）：让可填装/就绪的菜排前、同档位按菜谱书顺序固定，
        // 避免点击填装后菜谱乱动。只对 Cooking 面板注入，独立哨兵、幂等、抗 Steam 更新自修复。
        private const string SortSentinel = "CSE_AUTOPATCH_SORT";
        private const string SortOld = "State.lastRecipes = recipes || [];";
        private const string SortNew =
            "recipes = (recipes || []).slice().sort(function(a,b){var la=a.craftLocked?1:0,lb=b.craftLocked?1:0;if(la!==lb)return la-lb;var sa=a.status||0,sb=b.status||0;if(sa!==sb)return sb-sa;return (a.recipeId||0)-(b.recipeId||0);}); /* CSE_AUTOPATCH_SORT */ State.lastRecipes = recipes;";
        private const string GatherJs =
            "<script>" +
            "(function(){" +
            "if(window.__cseGatherLoaded)return;window.__cseGatherLoaded=1;/*CSE_GATHER_VER=13*/" +
            "function dbg(s){try{if(core&&core.UnitySendEvent)core.UnitySendEvent('CSE_DEBUG',{text:'[G] '+s});}catch(e){}}" +
"function toast(msg,ok){}" +
            "window.__cseWait=[];function notifyBag(d){var w=window.__cseWait;for(var i=w.length-1;i>=0;i--){var x=w[i];if(x.done)continue;var ok=false;try{ok=x.pred(d);}catch(e){ok=false;}if(ok){x.done=true;clearTimeout(x.to);w.splice(i,1);x.res(d);}}}" +
            "(function tryApply(){try{var ow=window.applyBagMsg;if(typeof ow==='function'&&!window.__cseWrappedBag){window.__cseWrappedBag=1;" +
            "window.applyBagMsg=function(d){try{notifyBag(d);}catch(e){}window.__cseLastBag=d;window.__cseBagSeq=(window.__cseBagSeq||0)+1;" +
            "if(d&&d.workbenchItems){window.__cseWbBag={bagItems:d.workbenchItems};window.__cseWbBagT=Date.now();}try{ow(d);}catch(e){}};return;}" +
            "}catch(e){}if(!window.__cseWrappedBag)setTimeout(tryApply,150);})();" +
            "try{if(core&&core.UnitySendEvent&&!window.__cseWrappedUE){window.__cseWrappedUE=1;var ue=core.UnitySendEvent;" +
            "core.UnitySendEvent=function(ev,data){" +
            "var ret=ue.apply(this,arguments);" +
            "if(ev==='RECIPE_CLICK'&&data&&data.recipeKey&&!window.__cseInternalClick){" +
            "var r=(typeof recipeByKey!=='undefined'&&recipeByKey)?recipeByKey[data.recipeKey]:null;" +
            "if(r){window.__cseRecipe=r;window.__cseMovedKey={};window.__cseScannedOwn={};window.__cseTimeoutOwn={};window.__cseEmptyOwn={};window.__cseUnacked=0;window.__cseCurSeen=0;window.__cseWbO=wbOwner();window.__cseWbBag=null;window.__cseWbBagT=0;var _st=Date.now();" +
            "setTimeout(async function(){var res='wait',_dl=_st+2800;while(Date.now()<_dl&&res==='wait'){res=await gather(r.recipeKey,_st);if(res==='wait')await sleep(220);}" +
"/*CSE_FIX_v6: 外层窗口到期兜底——旧逻辑当最后一次 gather 仍返回 'wait' 时整个流程静默结束（无 toast、无 CRAFT），而材料此刻其实已搬到工作台，正是\"材料都在但不制作\"。现在到期必须实盘复核一次：齐全就补发制作，不齐就明确提示，绝不静默。*/" +
"if(res==='wait'){(function fin(){var n=window.__cseFinN=(window.__cseFinN||0)+1;if(window.__cseBusy&&n<60){setTimeout(fin,100);return;}var ex=expectFromRecipe();if(!ex){dbg('窗口到期兜底：无配方明细，放弃');return;}var mm=calcInv(wbItems(),ex);if(mm.length){var bb=wbBag();if(bb&&!calcInv(bb,ex).length)mm=[];}if(!mm.length){dbg('窗口到期兜底：实测已备齐，补发制作');toast('材料已备齐，自动制作…',true);doCraft(ex);}else{dbg('窗口到期兜底：实测仍缺['+mm.join('|')+']，取消');toast('材料仍未备齐，已取消制作：'+mm.join('-'),false);}})();}},30);}" +
            "}" +
            "return ret;};}}catch(e){}" +
            "function waitBag(pred,ms){return new Promise(function(res,rej){var to=setTimeout(function(){if(x.done)return;x.done=true;var i=window.__cseWait.indexOf(x);if(i>=0)window.__cseWait.splice(i,1);rej(new Error('wait-timeout'));},ms||1500);var x={pred:pred,to:to,res:res,done:false};window.__cseWait.push(x);});}" +
            "function sleep(ms){return new Promise(function(r){setTimeout(r,ms);});}" +
            "function getMats(){var r=window.__cseRecipe;if(!r)return null;var a=[];if(r.materialsJson){try{a=JSON.parse(r.materialsJson);}catch(e){}}return a.filter(function(m){return m&&m.need>0&&m.name;});}" +
            "function wbItems(){return (typeof workbench!=='undefined'&&workbench&&workbench.getItems)?workbench.getItems():[];}" +
            "function wbInventory(){var m={};(wbItems()||[]).forEach(function(o){if(!o)return;if(o.name)m[o.name]=(m[o.name]||0)+(o.count||1);if(o.configId!=null){var ck='#cfg'+(parseInt(o.configId));m[ck]=(m[ck]||0)+(o.count||1);}if(o.itemId!=null){var ik='#id'+(parseInt(o.itemId));m[ik]=(m[ik]||0)+(o.count||1);}});return m;}" +
"function calcInv(src,expect){var cgc={},idc={},nmc={};(src||[]).forEach(function(o){if(!o)return;var c=o.count||1;if(o.configId!=null){var ck=''+(parseInt(o.configId));cgc[ck]=(cgc[ck]||0)+c;}if(o.itemId!=null){var ik=''+(parseInt(o.itemId));idc[ik]=(idc[ik]||0)+c;}var n=String(o.name||'').replace(/[\\s\\u3000]/g,'');if(n)nmc[n]=(nmc[n]||0)+c;});var out=[];for(var k in expect){var e=expect[k];var haveN=(e.configId!=null)?(cgc[''+(parseInt(e.configId))]||0):0;var bn=nmc[String(e.name||'').replace(/[\\s\\u3000]/g,'')]||0;if(bn>haveN)haveN=bn;if(e.itemId!=null){var bi=idc[''+(parseInt(e.itemId))]||0;if(bi>haveN)haveN=bi;}if(haveN<e.need)out.push(e.name);}return out;}" +
"function wbBag(){var w=window.__cseWbBag;if(w&&w.bagItems&&w.bagItems.length)return w.bagItems;var lb=window.__cseLastBag;if(lb&&lb.workbenchItems&&lb.workbenchItems.length)return lb.workbenchItems;return null;}" +
"function expectFromRecipe(){var r=window.__cseRecipe;if(!r)return null;var ms=getMats();if(!ms||!ms.length)return null;var ex={};ms.forEach(function(m){var k=m&&m.name;if(!k)return;if(!(k in ex))ex[k]={name:k,need:m.need||0,have:0,itemId:m.itemId||null,configId:m.configId||null};});return Object.keys(ex).length?ex:null;}" +
            "function wbOcc(){var occ={};(wbItems()||[]).forEach(function(o){if(!o)return;for(var i=0;i<(o.w||1);i++)for(var j=0;j<(o.h||1);j++)occ[(o.x+i)+','+(o.y+j)]=1;});return occ;}" +
            "function nextCell(){var lm=(typeof lastBagMsg!=='undefined'?lastBagMsg:{})||{};var cols=lm.workbenchCols||6,rows=lm.workbenchRows||5;var occ=window.__cseOcc||wbOcc();" +
            "for(var y=0;y<rows;y++)for(var x=0;x<cols;x++){var k=x+','+y;if(!occ[k]){occ[k]=1;return{x:x,y:y};}}return null;}" +
            "function wbOwner(){try{if(workbench&&workbench.getOwnerId)return workbench.getOwnerId();}catch(e){}return 0;}" +
            "function norm(s){return String(s||'').replace(/[\\s\\u3000]/g,'');}" +
            "function nameHit(a,b){var x=norm(a),y=norm(b);if(!x||!y)return false;return x===y;}" +
            "function countMat(bs,e){var n=0;if(!bs)return 0;for(var i=0;i<bs.length;i++){var o=bs[i];if(!o)continue;var c=o.count||1;var mm=false;if(e&&e.configId!=null&&o.configId!=null&&parseInt(o.configId)===parseInt(e.configId))mm=true;else if(e&&e.itemId!=null&&o.itemId!=null&&parseInt(o.itemId)===parseInt(e.itemId))mm=true;else if(e&&e.name&&o.name&&nameHit(o.name,e.name))mm=true;if(mm)n+=c;}return n;}" +
"/*CSE_FIX_v8: 实测证实——原生 ITEM_MOVE(整堆搬) 走官方 reducer，会被间歇性拒收/回滚（源柜纹丝不动），而 C# 的 BB_SPLIT_MOVE(拆取通道，同步 AddItem 直改权威库存) 次次成功。因此所有取料统一走 BB_SPLIT_MOVE（整堆=n 的堆当「拆 n」发，变相整搬）；每次仍等官方工作台回包确认数量到账才发下一次；未确认就打 __cseUnacked 让全扫补扫来源 */" +
"async function moveSync(k,e,it,target,_wb,needN){" +
"var bs0=wbBag();var b0=countMat(bs0,e);var sCnt=it.count||0;var movedCnt=(sCnt>needN)?needN:sCnt;var cell=nextCell();if(!cell){return -1;}" +
"core.UnitySendEvent('BB_SPLIT_MOVE',{itemId:parseInt(it.itemId)||0,configId:parseInt(it.configId)||0,count:movedCnt,fromOwnerId:target,toOwnerId:_wb,x:cell.x,y:cell.y});" +
"dbg('拆取已发 '+(it.name||k)+' '+target+'→'+_wb+' x'+movedCnt+' 堆'+sCnt+' id'+it.itemId+' cfg'+(it.configId||0));" +
"var dl=Date.now()+900;var seen=b0;while(Date.now()<dl){await sleep(50);var bx=wbBag();if(bx){var nn=countMat(bx,e);if(nn>=b0+movedCnt){seen=nn;break;}if(nn>seen)seen=nn;}}" +
"if(seen>=b0+movedCnt){e.have+=movedCnt;window.__cseMovedKey[k]=1;dbg('  移动已确认 +'+movedCnt+' 累计'+e.have+'/'+e.need);return movedCnt;}" +
"window.__cseUnacked=1;dbg('  移动未获官方确认(累计'+seen+'/'+(b0+movedCnt)+')，标记补扫');return 0;}" +
"async function fillFromCabinet(target,d,expect,order){" +
"var items=(d&&d.bagItems)||[];var sent=0;var _wb=wbOwner();var sameOwner=(_wb!==0&&target===_wb);var ms2={};" +
"for(var oi=0;oi<order.length;oi++){var key=order[oi];var e=expect[key];var needN=Math.max(0,e.need-e.have);if(needN<=0)continue;var kn=norm(key);var wantId=e.itemId;" +
"for(var ii=0;ii<items.length&&needN>0;ii++){var it=items[ii];if(!it||(it.count||0)<1)continue;if(ms2[it.itemId])continue;var idm=(wantId!=null&&((it.itemId!=null&&parseInt(it.itemId)===parseInt(wantId))||(it.configId!=null&&parseInt(it.configId)===parseInt(wantId))));var hmm=nameHit(it.name,key);if(!idm&&!hmm)continue;" +
"if(sameOwner){e.have+=Math.min(needN,it.count);sent++;ms2[it.itemId]=1;needN=Math.max(0,e.need-e.have);continue;}" +
"var c2=await moveSync(key,e,it,target,_wb,needN);if(c2<0){toast('工作台没有空格',false);return -1;}if(!c2)continue;ms2[it.itemId]=1;sent++;needN=Math.max(0,e.need-e.have);" +
"}}return sent;}" +
            "function itemNames(d){var o={};(d&&d.bagItems||[]).forEach(function(x){if(x&&x.name){var k=x.name+'#'+(x.itemId||0);o[k]=(x.count||1);}});var a=[];for(var k in o)a.push(k.replace('#', ' x')+'='+o[k]);return a.join(', ');}" +
            "function countByName(list){var m={};(list||[]).forEach(function(o){if(o&&o.name)m[o.name]=(m[o.name]||0)+(o.count||1);});return m;}" +
            "async function scanCabs(list,expect,order,tag,ms){" +
"if(ms==null)ms=1200;" +
"for(var ci=0;ci<list.length&&order.length;ci++){var t=list[ci];var target=t.ownerId;" +
"for(var rtry=0;rtry<2;rtry++){dbg((tag||'扫')+' 柜'+ci+' owner'+target+((rtry>0)?' 重试'+rtry:''));" +
"var prom=waitBag(function(x){return x&&x.bagOwnerId===target;},(rtry===0)?Math.min(700,(ms||1200)):ms);" +
"try{core.UnitySendEvent('TAB_SWITCH',{tab:t.idx});}catch(e){}" +
"var d;try{d=await prom;}catch(e){dbg('  等柜超时');if(rtry===0)continue;try{window.__cseTimeoutOwn[target]=t;}catch(_){}break;}" +
"try{if(d)window.__cseScannedOwn[target]=1;}catch(e){}" +
"dbg('  收到 owner'+d.bagOwnerId+' 物品'+(d.bagItems||[]).length+' ['+itemNames(d)+']');" +
            "dbg('  含缺料: '+order.map(function(k){var c=(d.bagItems||[]).reduce(function(a,it){return a+((it&&it.name&&nameHit(it.name,k))?(it.count||1):0);},0);return c>0?k+'x'+c:null;}).filter(Boolean).join(',')||'无');" +
"var sent=await fillFromCabinet(target,d,expect,order);if(sent<0)return -1;" +
"order=order.filter(function(k){return expect[k].have<expect[k].need;});" +
"try{if(!sent&&order.length&&(d.bagItems||[]).length>0)window.__cseEmptyOwn[target]=t;}catch(e){}" +
"dbg('  本柜搬'+sent+' 仍缺['+order.join('|')+'] 期望['+order.map(function(k){return expect[k].name;}).join(',')+']');break;}}return 0;}" +
            "function doCraft(expect){" +
            "return new Promise(function(res){var r=window.__cseRecipe;if(!r||!r.recipeKey){toast('未获得选中配方，请先点选配方',false);return res();}" +
            "setTimeout(function(){var seq0=window.__cseBagSeq||0;window.__cseInternalClick=1;try{core.UnitySendEvent('RECIPE_CLICK',{recipeKey:r.recipeKey});}catch(e){}window.__cseInternalClick=0;" +
            "var t0=Date.now();(function ws(){var got=((window.__cseBagSeq||0)>seq0);if(got||Date.now()-t0>1500){" +
            "var bsx=wbBag();var miss=expect?calcInv(wbItems(),expect):[];if(miss.length&&bsx)miss=calcInv(bsx,expect);" +
            "dbg('制作前复核 缺['+(miss.join('|')||'无')+'] 前台台上'+JSON.stringify(wbInventory())+' 后台袋'+(bsx?JSON.stringify(countByName(bsx)):'无'));" +
            "if(miss.length){toast('材料不足，未制作：'+miss.join('-'),false);return res();}" +
            "try{core.UnitySendEvent('CRAFT');}catch(e){}return res();}setTimeout(ws,60);})();},400);});}" +
            "async function verifyMats(expect){" +
            "var deadline=Date.now()+3500;" +
            "while(Date.now()<deadline){" +
            "var m0=calcInv(wbItems(),expect);if(!m0.length)return m0;" +
            "var bs=wbBag();if(bs&&!calcInv(bs,expect).length)return [];" +
            "dbg('仍缺['+m0.join('|')+'] 后台袋['+(bs?calcInv(bs,expect).join('|'):'无')+']');" +
            "await sleep(300);}" +
            "var bs2=wbBag();if(bs2&&!calcInv(bs2,expect).length)return [];" +
            "return calcInv(wbItems(),expect);}" +
            "async function ensureRecipeSelected(key){" +
            "if(!key)return;if(window.__cseRecipe&&window.__cseRecipe.recipeKey===key)return;" +
            "var before=window.__cseBagSeq||0;" +
            "window.__cseInternalClick=1;try{core.UnitySendEvent('RECIPE_CLICK',{recipeKey:key});}catch(e){}window.__cseInternalClick=0;" +
            "var deadline=Date.now()+1600;while(Date.now()<deadline&&((window.__cseBagSeq||0)<before+1)){await sleep(40);}}" +
            "async function gather(key,start){" +
            "if(window.__cseBusy)return 'wait';window.__cseBusy=1;var _st=start||Date.now();" +
            "try{" +
            "window.__cseWbO=window.__cseWbO||wbOwner();" +
            "if(key)await ensureRecipeSelected(key);" +
            "var r=window.__cseRecipe;if(!r||!r.recipeKey){await sleep(120);return 'wait';}" +
            "var mats=getMats();if(!mats||!mats.length){dbg('配方材料未就绪，等待…');return 'wait';}" +
            "try{dbg('配方材料: '+mats.map(function(m){return (m.name||'?')+'(need'+(m.need||0)+')'+(m.itemId?('/itemId'+m.itemId):'')+(m.configId?('/cfg'+m.configId):'');}).join(' | ')+' || keys:'+Object.keys(mats[0]||{}).join(','));}catch(e){}" +
            "var expect={},order=[];mats.forEach(function(m){var k=m.name;if(!(k in expect)){expect[k]={name:k,need:m.need||0,have:0,itemId:m.itemId||null,configId:m.configId||null};order.push(k);}});" +
            "var winv=wbInventory();order.forEach(function(k){var e=expect[k];var haveN=winv[k]||0;var _hc=(e.configId!=null)?(winv['#cfg'+(parseInt(e.configId))]||0):0;var _hi=(e.itemId!=null)?(winv['#id'+(parseInt(e.itemId))]||0):0;if(_hc>haveN)haveN=_hc;if(_hi>haveN)haveN=_hi;if(haveN)e.have=Math.max(e.have,haveN);});" +
            "order=order.filter(function(k){return expect[k].have<expect[k].need;});" +
            "dbg('配方['+(r.name||'')+'] 缺['+order.map(function(k){return expect[k].name+'('+expect[k].need+')';}).join('|')+'] 台上['+JSON.stringify(winv)+']');" +
            "if(!order.length){toast('材料已备齐，自动制作…',true);await doCraft(expect);return 'ok';}" +
            "var lm=(typeof lastBagMsg!=='undefined'?lastBagMsg:{})||{};var cabs=lm.cabinetTabs||[];var drawerO=lm.drawerOwnerId||0;var drawerShort=(lm.drawerShortCount||0);" +
            "if(!wbOwner()){return 'wait';}" +
            "function bpOwn(){try{return backpack&&backpack.getOwnerId?backpack.getOwnerId():0;}catch(e){return 0;}}" +
            "async function fillCurrent(){if(!order.length)return 0;var cd=(typeof window.__cseLastBag!=='undefined'&&window.__cseLastBag)?window.__cseLastBag:(typeof lastBagMsg!=='undefined'?lastBagMsg:null);" +
            "if(!cd||!cd.bagItems)return 0;var oc=cd.bagOwnerId||0;if(!oc||oc===wbOwner())return 0;" +
            "var ok=(oc===bpOwn())?1:0;for(var q=0;q<cabs.length;q++){if(cabs[q]&&cabs[q].ownerId===oc)ok=1;}if(oc===drawerO)ok=1;if(!ok)return 0;window.__cseCurSeen=oc;/*CSE_FIX_v9: 记录当前已直接取用的面板 owner，后续全扫/重扫不再重复扫它，省一次往返+可能的两轮超时*/" +
            "window.__cseOcc=wbOcc();var s=await fillFromCabinet(oc,cd,expect,order);" +
            "order=order.filter(function(k){return expect[k].have<expect[k].need;});" +
            "dbg('直接用已展示面板(owner'+oc+')搬'+s+' 仍缺['+order.join('|')+']');return s;}" +
            "function allSrcs(){var _wb=wbOwner(),cur=window.__cseUnacked?0:(window.__cseCurSeen||0),a=[],pp=bpOwn();if(pp&&pp!==cur)a.push({ownerId:pp,idx:0});if(drawerO&&drawerO!==_wb&&drawerO!==cur)a.push({ownerId:drawerO,idx:1});for(var i=0;i<cabs.length;i++){var c=cabs[i];if(c&&c.ownerId&&c.ownerId!==_wb&&c.ownerId!==cur)a.push({ownerId:c.ownerId,idx:2+i});}return a;}" +
            "window.__cseEmptyOwn=window.__cseEmptyOwn||{};await fillCurrent();" +
            "if(order.length){" +
            "var scan=[];if(drawerO&&drawerShort>0&&drawerO!==(window.__cseCurSeen||0))scan.push({ownerId:drawerO,idx:1});for(var i=0;i<cabs.length;i++){var c=cabs[i];if(c&&c.ownerId&&(c.shortCount||0)>0&&c.ownerId!==(window.__cseCurSeen||0))scan.push({ownerId:c.ownerId,idx:2+i});}" +
"if(scan.length){window.__cseOcc=wbOcc();window.__cseLastBag=null;window.__cseScannedOwn={};window.__cseTimeoutOwn={};window.__cseWbO=wbOwner();window.__cseWbBag=null;" +
            "dbg('官方标缺口柜 '+scan.length+' 个，工作台owner'+wbOwner());toast('正在从缺口柜补齐材料…',true);" +
            "if(await scanCabs(scan,expect,order,'缺')<0)return 'ok';" +
            "window.__cseOcc=wbOcc();window.__cseLastBag=null;}}else{toast('材料已备齐，自动制作…',true);}" +
            "if(order.length){var full=allSrcs().filter(function(x){return window.__cseUnacked||!(window.__cseScannedOwn||{})[x.ownerId];});" +
            "dbg('全扫源 '+full.length+'/总'+allSrcs().length+' 柜tab'+cabs.length+' 背包o'+ ((function(){try{return backpack.getOwnerId?backpack.getOwnerId():0;}catch(e){return -1;}})()) +' 工作台o'+wbOwner() );" +
            "dbg('源厂家 ['+full.map(function(x){return 'o'+x.ownerId;}).join(',')+']');" +
            "if(full.length){dbg('仍缺['+order.join('|')+']，全扫(含背包)补齐');toast('储物柜/背包补齐…',true);" +
"window.__cseOcc=wbOcc();window.__cseLastBag=null;if(await scanCabs(full,expect,order,'全')<0)return 'ok';}}" +
"if(order.length){var to=[];for(var _oo in (window.__cseTimeoutOwn||{})){var tt=window.__cseTimeoutOwn[_oo];if(tt&&tt.ownerId!==(window.__cseCurSeen||0))to.push(tt);}" +
"if(to.length){dbg('再扫 '+to.length+' 个超时柜(内容确定性匹配缺失的空柜不再重扫)');toast('重扫超时柜…',true);" +
"window.__cseOcc=wbOcc();window.__cseLastBag=null;if(await scanCabs(to,expect,order,'重',1500)<0)return 'ok';}}" +
            "if(order.length){var any=order.some(function(k){return expect[k].have>0;});if(any&&Date.now()-_st<2500){dbg('部分搬到['+order.join('|')+']，窗口内重扫补齐');return 'wait';}" +
            "if(!any){dbg('一处都没搬到['+order.join('|')+']，取消制作');toast('各处都找不到：'+order.join('-')+'，已取消制作',false);return 'cancel';}}" +
            "if(window.__cseUnacked)await sleep(900);/*CSE_FIX_v9: 全部移动均已获官方确认时，无需再固定等 900ms 沉降，直接进入实盘复核——显著缩短首次制作耗时*/" +
            "dbg('期望明细['+Object.keys(expect).map(function(k){return expect[k].name+' hv'+expect[k].have+'/'+expect[k].need;}).join(' | ')+']');" +
            "/*CSE_FIX_v5: 两处收口——① 备齐判定不许再用\"已发送移动\"当成功（旧 movedAll 早退会让材料其实没到位时也发 CRAFT，官方必判失败、残留那几件又让每次点配方都 +1）；② 制作前不再只等 90ms，而是等官方 RECIPE_CLICK 触发的原生补料回包落地后，用实盘数据复核一次再发 CRAFT*/" +
            "var _miss=await verifyMats(expect);" +
            "if(_miss&&_miss.length){dbg('材料不足[实测]'+_miss.join('|')+'，取消制作，不发CRAFT');toast('材料不足，已取消制作：'+_miss.join('-'),false);return 'cancel';}" +
            "dbg('材料已备齐(实测工作台真实实物)，进入自动制作，交由官方后端最终判定');" +
            "toast('自动制作中…',true);" +
            "await doCraft(expect);" +
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
                ApplyRecipeSort(log);
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

                const string VerMarker = "CSE_GATHER_VER=13";
                // 已打且版本最新：跳过
                if (text.Contains(GatherSentinel) && text.Contains(VerMarker)) return;

                // 存在旧版注入：整块干净移除（避免新旧脚本并存、全局函数/变量互相覆盖）。
                // ★2026-09-20 修复严重 bug：旧正则 `[\s\S]*?__cseGatherLoaded[\s\S]*?` 未把 __cseGatherLoaded 锚定到
                //   同一个 <script> 块内，重新注入（版本号升级触发）时它会从 HTML 更前面的官方 <script> 起吃，
                //   把整页游戏脚本与我们的旧脚本一起删掉，导致 ToolTable 页面被打空、面板打不开。
                //   新正则用 (?!</?script[^>]*>) 负向断言，保证不会跨过任何其它 <script>/</script>。
                var re = new Regex(
                    "<script[^>]*>(?:(?!</?script[^>]*>)[\\s\\S])*?__cseGatherLoaded(?:(?!</?script[^>]*>)[\\s\\S])*?</script>\\s*<!--\\s*" + GatherSentinel + "\\s*-->",
                    RegexOptions.IgnoreCase);
                text = re.Replace(text, "");

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
                log.LogInfo("[CookingSourceExpand] 已给 ToolTable.html 打上『点击配方补齐并制作』补丁（按需拆取版）。");
            }
            catch (Exception e)
            {
                log.LogWarning($"[CookingSourceExpand] 『点击配方补齐并制作』补丁失败：{e.Message}");
            }
        }

        /// <summary>仅对烹饪面板注入「菜谱稳定排序」（独立哨兵，幂等，抗更新）。只改前端排序，不碰任何物品数据。</summary>
        private static void ApplyRecipeSort(ManualLogSource log)
        {
            try
            {
                var path = Path.Combine(UiRoot, "Cooking", "Cooking.html");
                if (!File.Exists(path))
                {
                    log.LogWarning("[CookingSourceExpand] Cooking.html 不存在，「菜谱稳定排序」补丁跳过。");
                    return;
                }
                string text;
                try { text = File.ReadAllText(path, new UTF8Encoding(false)); }
                catch (Exception e) { log.LogWarning($"[CookingSourceExpand] 读取 Cooking.html 失败：{e.Message}"); return; }

                if (text.Contains(SortSentinel)) return; // 已打过，跳过

                if (text.IndexOf(SortOld, StringComparison.Ordinal) < 0)
                {
                    log.LogWarning("[CookingSourceExpand] Cooking.html 未找到排序注入点（游戏可能已更新变量名），『菜谱稳定排序』跳过。");
                    return;
                }

                var bak = path + ".cse.bak";
                if (!File.Exists(bak))
                {
                    try { File.WriteAllText(bak, text, new UTF8Encoding(false)); }
                    catch (Exception e) { log.LogWarning($"[CookingSourceExpand] 备份 Cooking.html 失败：{e.Message}"); }
                }

                text = text.Replace(SortOld, SortNew);
                File.WriteAllText(path, text, new UTF8Encoding(false));
                log.LogInfo("[CookingSourceExpand] 已给 Cooking.html 打上『菜谱稳定排序』补丁。");
            }
            catch (Exception e)
            {
                log.LogWarning($"[CookingSourceExpand] 『菜谱稳定排序』补丁失败：{e.Message}");
            }
        }

        // ===== 游戏内堆叠可视化开关（v2.0.11）=====
        // 独立哨兵 + 版本标记，幂等、抗 Steam 更新。向 ToolTable.html 注入右上角「堆叠:开/关」固定按钮：
        // 点击 → core.UnitySendEvent('CSE_TOGGLE_STACK',{on:!cur}) → C# 切换运行时总开关并写回 cfg。
        // 初始状态以 <meta name="cse-stack-state" content="0/1"> 持久在 HTML 里（C# 每次切换后重写），
        // 保证按钮状态、cfg、运行时状态三者一致。
        private const string StackToggleSentinel = "CSE_AUTOPATCH_STACKTOGGLE";
        private const string StackToggleVer = "CSE_STACKTOGGLE_VER=1";

        private const string StackToggleHtml =
            "<meta name=\"cse-stack-state\" content=\"__CSE_STACK_INIT__\" data-cse=\"1\" />\n" +
            "<style data-cse=\"1\">" +
            "#cseStackToggle{position:fixed;right:12px;top:12px;z-index:99999;padding:8px 16px;font-size:15px;font-weight:700;" +
            "border:2px solid rgba(255,255,255,.55);border-radius:8px;cursor:pointer;user-select:none;" +
            "box-shadow:0 2px 10px rgba(0,0,0,.45);font-family:Arial,'Microsoft YaHei',sans-serif;}" +
            "#cseStackToggle.on{background:rgba(46,160,67,.92);color:#fff;}" +
            "#cseStackToggle.off{background:rgba(219,68,55,.92);color:#fff;}" +
            "</style>\n" +
            "<script>" +
            "(function(){if(window.__cseStackToggleLoaded)return;window.__cseStackToggleLoaded=1;/*CSE_STACKTOGGLE_VER=1*/" +
            "var init=1;try{var m=document.querySelector('meta[name=\"cse-stack-state\"]');if(m&&m.getAttribute&&m.getAttribute('content')==='0')init=0;}catch(e){}" +
            "window.__cseStackOn=!!init;" +
            "function paint(){var b=document.getElementById('cseStackToggle');if(!b)return;b.textContent=window.__cseStackOn?'堆叠:开':'堆叠:关';b.className=window.__cseStackOn?'on':'off';}" +
            "function mk(){if(document.getElementById('cseStackToggle'))return;var b=document.createElement('button');b.id='cseStackToggle';b.type='button';" +
            "b.addEventListener('click',function(){window.__cseStackOn=!window.__cseStackOn;paint();try{if(core&&core.UnitySendEvent)core.UnitySendEvent('CSE_TOGGLE_STACK',{on:window.__cseStackOn});}catch(e){}});" +
            "document.body.appendChild(b);paint();}" +
            "function loop(){mk();var n=0;var t=setInterval(function(){if(document.getElementById('cseStackToggle')){clearInterval(t);return;}mk();if(++n>300)clearInterval(t);},300);}" +
            "if(document.readyState!=='loading')loop();else document.addEventListener('DOMContentLoaded',loop);" +
            "})();" +
            "</script>";

        /// <summary>注入 / 刷新工作台右上角「堆叠:开/关」按钮（internal：Stack.Plugin.Init 注入，CSE_TOGGLE_STACK 事件刷新状态）。</summary>
        internal static void ApplyStackToggle(ManualLogSource log, bool on)
        {
            try
            {
                var path = Path.Combine(UiRoot, "ToolTable", "ToolTable.html");
                if (!File.Exists(path))
                {
                    log.LogWarning("[堆叠可视化开关] ToolTable.html 不存在，补丁跳过。");
                    return;
                }
                string text;
                try { text = File.ReadAllText(path, new UTF8Encoding(false)); }
                catch (Exception e) { log.LogWarning($"[堆叠可视化开关] 读取 ToolTable.html 失败：{e.Message}"); return; }

                if (text.Contains(StackToggleSentinel))
                {
                    if (text.Contains(StackToggleVer))
                    {
                        // 已打且版本最新：只刷新初始状态 meta（下次打开面板，按钮初始即当前开关状态）
                        WriteStackToggleState(path, text, log, on);
                    }
                    else
                    {
                        // 旧版本注入：整块移除后按新版本重打
                        text = RemoveStackToggleBlock(text);
                        InjectStackToggleBlock(path, text, log, on);
                    }
                    return;
                }

                // 首次打：备份原文件
                var bak = path + ".cse.bak";
                if (!File.Exists(bak))
                {
                    try { File.WriteAllText(bak, text, new UTF8Encoding(false)); }
                    catch (Exception e) { log.LogWarning($"[堆叠可视化开关] 备份 ToolTable.html 失败：{e.Message}"); }
                }
                InjectStackToggleBlock(path, text, log, on);
            }
            catch (Exception e)
            {
                log.LogWarning($"[堆叠可视化开关] 补丁失败：{e.Message}");
            }
        }

        private static void InjectStackToggleBlock(string path, string text, ManualLogSource log, bool on)
        {
            string block = StackToggleHtml.Replace("__CSE_STACK_INIT__", on ? "1" : "0");
            int bodyIdx = text.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyIdx >= 0)
            {
                text = text.Substring(0, bodyIdx) + block + "\n<!-- " + StackToggleSentinel + " -->\n" + text.Substring(bodyIdx);
            }
            else
            {
                text = text + "\n<!-- " + StackToggleSentinel + " -->";
            }
            File.WriteAllText(path, text, new UTF8Encoding(false));
            string state = on ? "开" : "关";
            log.LogInfo($"[堆叠可视化开关] 已给 ToolTable.html 打上『堆叠:开/关』按钮（初始：{state}）。");
        }

        private static void WriteStackToggleState(string path, string text, ManualLogSource log, bool on)
        {
            string v = on ? "1" : "0";
            var re = new Regex("<meta name=\"cse-stack-state\" content=\"[01]\"", RegexOptions.IgnoreCase);
            if (re.IsMatch(text))
            {
                text = re.Replace(text, "<meta name=\"cse-stack-state\" content=\"" + v + "\"", 1);
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            string state = on ? "开" : "关";
            log.LogInfo($"[堆叠可视化开关] 状态已刷新 → {state}。");
        }

        private static string RemoveStackToggleBlock(string text)
        {
            // 整块移除（meta 起始 → 哨兵注释结束），负向结构保证不跨其它 script
            var re = new Regex(
                "\\s*<meta name=\"cse-stack-state\"[^>]*/>[\\s\\S]*?<script[^>]*>[\\s\\S]*?__cseStackToggleLoaded[\\s\\S]*?</script>\\s*<!--\\s*" + StackToggleSentinel + "\\s*-->",
                RegexOptions.IgnoreCase);
            return re.Replace(text, "");
        }
    }
}