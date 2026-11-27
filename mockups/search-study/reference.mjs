// Thumbnail crop boxes are in the supplied Windows captures (not full-page backgrounds).
export const groups=[
 {file:'upload_fs102cj1.png',x:[240,540,840,1140,1440],y:181,w:284,h:160,items:[
 ['Miss Malfoy | Episode 1: The Goblet Chose Him','Halcyon Tide','8:48','2 days ago'],['Final 2:59 INSANE ENDING Trail Blazers at Knicks','NBA','5:40','2 days ago'],['5 One Pot Meals for When You Can’t Be Bothered','Andy Cooks','21:42','12 days ago'],['FINAL FANTASY VI Reimagined – The Movie (Part 2)','FusionVerse Production','13:05','12 days ago'],['Lumos Lumos Anime Season 3 [Official Music Video]','ADayASMR','1:09','1 month ago']]},
 {file:'upload_fs102cj1.png',x:[240,540,840,1140,1440],y:507,w:284,h:160,items:[
 ['iShowSpeed Reacts To GTA 6’s SAD ENDING','Speedy Boykins','8:37','4 days ago'],['GTA 6 – Exploring Vice Beach as Lucia!','DubStepZz','34:01','3 days ago'],['iShowSpeed THROWS A Guy Into A Spinning Boat Blade','Speedy Boykins','0:32','5 days ago'],['iShowSpeed Finds A HULK-LIKE MONSTER In GTA 6','Speedy Boykins','2:09','5 days ago'],['The Dark Reality of “GTA 6 on PC” Right Now','InRecord','10:47','2 days ago']]},
 {file:'upload_l26ue2j4.png',x:[528,868,1208,1548],y:146,w:324,h:182,items:[
 ['PBS News Hour live episode, Nov. 24, 2026','PBS NewsHour','LIVE',''],['News Wrap: California’s king tides could be biggest in 4 decades','PBS NewsHour','6:13','2 minutes ago'],['What’s next for Nvidia and Jensen Huang after Trump’s intervention','PBS NewsHour','6:39','10 minutes ago'],['How Trump may have profited from Huang’s return to Nvidia','PBS NewsHour','5:15','19 minutes ago']]},
 {file:'upload_l26ue2j4.png',x:[528,868,1208,1548],y:425,w:324,h:182,items:[
 ['Nvidia brings back Jensen Huang. But at what cost? | The Current','CBC News','15:44','51 minutes ago'],['What happens if Quebec’s legislators sit without swearing oaths?','CBC News','5:46','1 hour ago'],['Premier League Review: Matchweek 11 (2026–27) | NBC Sports','NBC Sports','51:37','1 hour ago'],['Nobody asked why she kept running away | Ep. 1 | No Safe Place','CBC News','44:14','2 hours ago']]},
 {file:'upload_t6atsepa.png',x:[240,650,1060,1470],y:172,w:394,h:220,items:[
 ['How Good is 7’9 Olivier Rioux Actually?','Hoop County','13:59',''],['Winter Outing — A World Where Malfoy Is the Heroine [Harry Potter & Malfoy]','비밀포터','10:46',''],['I Think Luka Dončić Has Solved Basketball','Henry Hoops','11:27',''],['Lumos Lumos','MJChipmunks','44:49','']]},
 {file:'upload_t6atsepa.png',x:[240,650,1060,1470],y:490,w:394,h:220,items:[
 ['128GB AI Desktops… HUGE Price difference (ACTUALLY Tested)','Alex Ziskind','24:57',''],['The Holy Grail of Final Fantasy X Skips Was Just Solved','DoctorSwellman','8:43',''],['ROCKETS AT NUGGETS | EMIRATES NBA CUP | FULL GAME HIGHLIGHTS','NBA','18:06',''],['Claude Can Design ANYTHING in This FREE CAD Software!','Print N Repeat','17:04','']]}
];
export const videos=groups.flatMap(g=>g.items).map(([title,channel,duration,age],id)=>({title,channel,duration,age,id}));
