using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Dom.Html;
using AngleSharp.Parser.Html;
using Conversive.PHPSerializationLibrary;
using MySql.Data.MySqlClient;

namespace WSDBBuilder
{
    class Program
    {
        static readonly StreamWriter Consolelog = new StreamWriter(new FileStream("output.log", FileMode.Create), Encoding.UTF8);
        static void Log(string message)
        {
            Consolelog.WriteLine(message);
            Console.WriteLine(message);
        }
        static void Pause()
        {
            Console.Write("Press any key to continue . . .");
            Console.ReadKey();
            Console.Write("\b \b\r\n");
        }
        static bool GetId(out List<short> ids, bool en = false)
        {
            string result;
            if (!RequestPage(en ? "http://ws-tcg.com/en/cardlist/list" : "http://ws-tcg.com/cardlist", out result))
            {
                ids = null;
                return false;
            }
            var page = new HtmlParser(result).Parse();
            IEnumerable<IElement> matches = page.DocumentElement.QuerySelectorAll("a[onclick^=showExpansionDetail]");
            ids = new List<short>();
            foreach (var node in matches)
            {
                var att = node.Attributes.First(attr => attr.Name == "onclick");
                var match = Regex.Match(att.Value, @"showExpansionDetail\('(\d(?:(?:\d)?)+)'");
                ids.Add(Convert.ToInt16(match.Groups[1].Value));
            }
            ids.Sort();
            Log("Retrieved " + ids.Count + " set id");
            return true;
        }
        static void GetSets(out List<string[]> sets, List<short> ids, bool en = false)
        {
            var index = 0;
            sets = new List<string[]>();
            while (index < ids.Count)
            {
                var pairs = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("expansion_id", ids[index].ToString())
                };
                string result;
                Log("Getting set " + ids[index]);
                if (PostRequest(pairs, "/jsp/cardlist/expansionHeader", out result, en))
                {
                    var page = new HtmlParser(result).Parse();
                    var match = page.DocumentElement.QuerySelector("h3");
                    var seperator = new[] { " - " };
                    var split = match.TextContent.Split(seperator, StringSplitOptions.None);
                    sets.Add(new[] { ids[index].ToString(), split[1], split[0] });
                    index++;
                }
                else
                    if (!Prompt("Retry?"))
                        index++;

            }
        }
        [SuppressMessage("ReSharper", "AssignNullToNotNullAttribute")]
        static bool RequestPage(string url, out string result)
        {
            try
            {
                var wclient = new WebClient { Encoding = Encoding.UTF8 };
                result = new StreamReader(wclient.OpenRead(url), wclient.Encoding).ReadToEnd();
                return true;
            }
            catch (Exception e)
            {
                Log(e.ToString());
                result = null;
            }
            return false;
        }
        static bool PostRequest(IEnumerable<KeyValuePair<string, string>> pairs, string location, out string result, bool en = false)
        {
            using (var client = new HttpClient())
            {
                location = (en ? "http://ws-tcg.com/en" : "http://ws-tcg.com") + location;
                var encpair = new FormUrlEncodedContent(pairs);
                try
                {
                    result = client.PostAsync(location, encpair).Result.Content.ReadAsStringAsync().Result;
                }
                catch (AggregateException e)
                {
                    Log(e.ToString());
                    result = null;
                    Pause();
                    return false;
                }
            }
            return true;
        }
        static bool Prompt(string message)
        {
            Log(message + " y/n");
            ConsoleKeyInfo key;
            do
            {
                key = Console.ReadKey();
            } while (key.Key.ToString().ToLower() != "y" && key.Key.ToString().ToLower() != "n");
            Console.WriteLine();

            Consolelog.WriteLine(key.Key.ToString());

            if (key.Key.ToString().ToLower() == "y")
                return true;
            else
                return false;
        }
        static void ProcessSets(List<short> setIds, List<string[]> sets, MySqlConnection conn, bool en = false)
        {
            if (setIds == null) throw new ArgumentNullException("setIds");
            foreach (var set in sets)
            {
                switch (set[2])
                {
                    case "ブースターパック":
                        set[2] = "BP";
                        break;
                    case "エクストラパック":
                        set[2] = "EP";
                        break;
                    case "トライアルデッキ":
                        set[2] = "TD";
                        break;
                    case "PRカード":
                        set[2] = "PR";
                        break;
                    case "エクストラパック／エクストラブースター/他":
                        set[2] = "OT";
                        break;
                    case "Booster Pack":
                        set[2] = "BP";
                        break;
                    case "Extra Pack / Extra Booster":
                        set[2] = "EP";
                        break;
                    case "Trial Deck":
                        set[2] = "TD";
                        break;
                    case "PR Card":
                        set[2] = "PR";
                        break;
                    default:
                        Log("Unknown set identifier: " + set[2] + " please enter value");
                        set[2] = Console.ReadLine();
                        break;
                }
                Log("Inserting set " + set[1] + " into database with id " + set[0] + " and type " + set[2]);

                try
                {
                    var cmd = new MySqlCommand
                    {
                        Connection = conn,
                        CommandText =
                            "INSERT INTO ws_" + (en ? "en" : "jp") + "sets(id,name,type) VALUES(@id,@name,@type)"
                    };

                    cmd.Prepare();

                    cmd.Parameters.AddWithValue("@id", set[0]);
                    cmd.Parameters.AddWithValue("@name", set[1]);
                    cmd.Parameters.AddWithValue("@type", set[2]);

                    cmd.ExecuteNonQuery();
                }
                catch (MySqlException e)
                {
                    Log(e.ToString());
                    if (Prompt("Exit?"))
                        return;
                }
            }
        }
        static int GetPageCount(int setId, bool en = false)
        {
            string result;
            var pairs = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("expansion_id", setId.ToString())
            };
            if (!PostRequest(pairs, "/jsp/cardlist/expansionDetail", out result, en)) return 0;
            var page = new HtmlParser(result).Parse();
            //get the string that has the total number of cards
            var noCardsText = page.DocumentElement.QuerySelector("p.pageLink").FirstChild.TextContent.Trim();
            var nocards = Regex.Match(noCardsText, @"\[(?:\d)+ - (?:\d)+ \/ (\d+)\]");
            //calculate the number of pages
            return (int)Math.Ceiling((Convert.ToDouble(nocards.Groups[1].Value) / 10));
        }
        static void DownloadCardImage(string url, string filename, bool en = false)
        {
            Log("Saving "+filename);
            Directory.CreateDirectory(Directory.GetCurrentDirectory() + @"\cardimages");
            var wclient = new WebClient();
            wclient.DownloadFile("http://ws-tcg.com/" + (en ? "en/" : "") + "cardlist/cardimages/" + filename, Directory.GetCurrentDirectory() + @"\cardimages\" + filename);
        }
        static IEnumerable<string> GetCardIds(int setId, int nopages, bool en = false)
        {
            var cardIds = new List<string>();
            for (var i = 1; i <= nopages; i++)
            {
                var pairs = new List<KeyValuePair<string, string>>
                        {
                            new KeyValuePair<string, string>("expansion_id", setId.ToString()),
                            new KeyValuePair<string, string>("page", i.ToString())
                        };
                string result;
                if (PostRequest(pairs, "/jsp/cardlist/expansionDetail", out result, en))
                {
                    var page = new HtmlParser(result).Parse();
                    IEnumerable<IElement> matches = page.DocumentElement.QuerySelectorAll("tr td:first-child");
                    cardIds.AddRange(from m in matches where !String.IsNullOrWhiteSpace(m.TextContent) select m.TextContent.Trim());
                }
                else
                    if (Prompt("Retry?"))
                        i--;
            }
            return cardIds;
        }
        static void ImportLibrary(IReadOnlyList<short> setIds, MySqlConnection conn, bool en = false)
        {
            var index = 0;
            var serializer = new Serializer();
            while (index < setIds.Count)
            {
                Log("Processing set " + setIds[index]);
                var nopages = GetPageCount(setIds[index], en);
                Log("Set " + setIds[index] + " has " + nopages + " pages");
                var cardIds = GetCardIds(setIds[index], nopages, en);
                foreach (var id in cardIds)
                {
                    string result;
                    RequestPage("http://ws-tcg.com" + (en ? "/en/cardlist/list/?cardno=" : "/cardlist/?cardno=") + id, out result);
                    var page = new HtmlParser(result).Parse();
                    var image = page.DocumentElement.QuerySelector(".status td img").Attributes.First(attr => attr.Name == "src").Value;
                    var filename = Regex.Match(image, @".*\/+(.+\.\w+)").Groups[1].Value;
                    DownloadCardImage("http://ws-tcg.com/"+(en ? "en/" : "")+"cardlist/cardimages/"+filename, filename, en);
                    var matches = page.DocumentElement.QuerySelectorAll(".status td");
                    var tableNodes = matches.ToList();
                    var card = new Dictionary<string, string>();
                    card.Add("name", tableNodes[1].FirstChild.TextContent.Trim());
                    card.Add("kana", tableNodes[1].ChildNodes.First(node => node.NodeName == "SPAN").TextContent.Trim());
                    //tableNodes[2] == id
                    card.Add("rarity", tableNodes[3].TextContent.Trim());
                    card.Add("set", setIds[index].ToString()); //tableNodes[4] holds the expansion but it's best to store the id instead
                    card.Add("side", Regex.Match(tableNodes[5].InnerHtml.Trim(), "src=['\"](?:.+\\/)+(\\w)").Groups[1].Value.ToUpper());
                    switch (tableNodes[6].TextContent.Trim())
                    {
                        case "クライマックス":
                            card.Add("type", "CX");
                            break;
                        case "イベント":
                            card.Add("type", "EV");
                            break;
                        case "キャラ":
                            card.Add("type", "CH");
                            break;
                        case "Climax":
                            card.Add("type", "CX");
                            break;
                        case "Event":
                            card.Add("type", "EV");
                            break;
                        case "Character":
                            card.Add("type", "CH");
                            break;
                        default:
                            Log("Unknown card type: " + tableNodes[6].TextContent.Trim() + " please enter a value");
                            string readLine;
                            do
                                readLine = Console.ReadLine();
                            while (readLine == null);
                            card.Add("type", readLine.Trim());
                            break;
                    }
                    card.Add("color", Regex.Match(tableNodes[7].InnerHtml.Trim(), "src=['\"](?:.+\\/)+(\\w)").Groups[1].Value.ToUpper());
                    card.Add("level", Regex.IsMatch(tableNodes[8].TextContent.Trim(), "[-－]") ? null : tableNodes[8].TextContent.Trim());
                    card.Add("cost", Regex.IsMatch(tableNodes[9].TextContent.Trim(), "[-－]") ? null : tableNodes[9].TextContent.Trim());
                    card.Add("power", Regex.IsMatch(tableNodes[10].TextContent.Trim(), "[-－]") ? null : tableNodes[10].TextContent.Trim());
                    card.Add("soul", Regex.Matches(tableNodes[11].InnerHtml.Trim(), "src=['\"](?:.+?\\/)+?(\\w+)?\\.").Count.ToString());
                    var triggers = new ArrayList();
                    var rmatches = Regex.Matches(tableNodes[12].InnerHtml, "src=['\"](?:.+?\\/)+?(\\w+)?\\.");
                    foreach (Match m in rmatches)
                        triggers.Add(m.Groups[1].Value);
                    card.Add("triggers", triggers.Count == 0 ? null : serializer.Serialize(triggers));
                    var traits = new ArrayList();
                    if (tableNodes[13].TextContent.Trim().Contains('・'))
                    {
                        var s = tableNodes[13].TextContent.Split('・');
                        foreach (var item in s)
                        {
                            if (!Regex.IsMatch(item.Trim(), "[-－]"))
                                traits.Add(item.Trim());
                        }
                    }
                    card.Add("traits", traits.Count == 0 ? null : serializer.Serialize(traits));

                    card.Add("text",
                        !Regex.IsMatch(tableNodes[14].TextContent.Trim(), "(?:（バニラ）|[-－])")
                            ? Regex.Replace(tableNodes[14].InnerHtml.Trim(), "<br\\s*(?:\\/)*>$", "",
                                RegexOptions.None)
                            : null);

                    card.Add("flavor",
                        !Regex.IsMatch(tableNodes[14].TextContent.Trim(), "[-－]")
                            ? Regex.Replace(tableNodes[15].InnerHtml.Trim(), "<br\\s*(?:\\/)*>$", "",
                                RegexOptions.None)
                            : null);

                    Log("Adding " + id + " to the database.");
                    try
                    {
                        var cmd = new MySqlCommand
                        {
                            Connection = conn,
                            CommandText = string.Format("INSERT INTO " +
                                "ws_{0}cards(cardno,name,kana,rarity,expansion,side,color,level,cost,power,soul,triggers,traits,text,flavor) " +
                                "VALUES(@cardno,@name,@kana,@rarity,@expansion,@side,@color,@level,@cost,@power,@soul,@triggers,@traits,@text,@flavor)", (en ? "en" : "jp"))
                        };

                        cmd.Prepare();

                        cmd.Parameters.AddWithValue("@cardno", id);
                        cmd.Parameters.AddWithValue("@name", card["name"]);
                        cmd.Parameters.AddWithValue("@kana", card["kana"]);
                        cmd.Parameters.AddWithValue("@rarity", card["rarity"]);
                        cmd.Parameters.AddWithValue("@expansion", setIds[index]);
                        cmd.Parameters.AddWithValue("@side", card["side"]);
                        cmd.Parameters.AddWithValue("@color", card["color"]);
                        cmd.Parameters.AddWithValue("@level", card["level"]);
                        cmd.Parameters.AddWithValue("@cost", card["cost"]);
                        cmd.Parameters.AddWithValue("@power", card["power"]);
                        cmd.Parameters.AddWithValue("@soul", card["soul"]);
                        cmd.Parameters.AddWithValue("@triggers", card["triggers"]);
                        cmd.Parameters.AddWithValue("@traits", card["traits"]);
                        cmd.Parameters.AddWithValue("@text", card["text"]);
                        cmd.Parameters.AddWithValue("@flavor", card["flavor"]);

                        cmd.ExecuteNonQuery();
                    }
                    catch (MySqlException e)
                    {
                        Log(e.ToString());
                        if (Prompt("Exit?"))
                            return;
                    }
                }
                index++;

            }
        }
        static void Main()
        {
            string input;
            Consolelog.AutoFlush = true;
            MySqlConnection conn = null;
            const string cs = @"server=localhost;userid=root;password=;database=ws_db;charset=utf8;";
            try
            {
                conn = new MySqlConnection(cs);
                conn.Open();
            }
            catch (MySqlException e)
            {
                Log(e.ToString());
                Log("Database connection failed!");
                Pause();
                Environment.Exit(1);
            }
            do
            {
                Console.WriteLine("What would you like to do?\n" +
                    "1. Impot JP expansions\n" +
                    "2. Import JP library\n" +
                    "3. Import EN expansions\n" +
                    "4. Import EN library\n" +
                    "5. Exit");
                input = Console.ReadLine();
                List<short> setIds;
                switch (input)
                {
                    case "1":
                        if (GetId(out setIds))
                        {
                            List<string[]> sets;
                            GetSets(out sets, setIds);
                            ProcessSets(setIds, sets, conn);
                        }
                        break;
                    case "2":
                        if (GetId(out setIds))
                            ImportLibrary(setIds, conn);
                        break;
                    case "3":
                        if (GetId(out setIds, true))
                        {
                            List<string[]> sets;
                            GetSets(out sets, setIds, true);
                            ProcessSets(setIds, sets, conn, true);
                        }
                        break;
                    case "4":
                        if (GetId(out setIds, true))
                            ImportLibrary(setIds, conn, true);
                        break;
                }

            } while (input != "5");
            //close mysql connection
            conn.Close();
            Consolelog.Close();
        }
    }
}
