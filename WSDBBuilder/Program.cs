using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Dom.Html;
using AngleSharp.Parser.Html;
using MySql.Data.MySqlClient;
using Conversive.PHPSerializationLibrary;

namespace WSDBBuilder
{
    class Program
    {
        static StreamWriter consolelog = new StreamWriter(new FileStream("output.log", FileMode.Create), Encoding.UTF8);
        static void log(string message)
        {
            consolelog.WriteLine(message);
            Console.WriteLine(message);
        }

        static void pause()
        {
            Console.Write("Press any key to continue . . .");
            Console.ReadKey();
            Console.Write("\b \b\r\n");
        }
        static bool getId(ref List<short> ids)
        {
            string result = "";
            if (!requestPage("http://ws-tcg.com/cardlist", ref result))
                return false;
            IHtmlDocument page = new HtmlParser(result).Parse();
            IEnumerable<IElement> matches = page.DocumentElement.QuerySelectorAll("a[onclick^=showExpansionDetail]");
            foreach (IElement node in matches)
            {
                IAttr att = node.Attributes.First(attr => attr.Name == "onclick");
                Match match = Regex.Match(att.Value, "showExpansionDetail\\('(\\d(?:(?:\\d)?)+)'");
                ids.Add(Convert.ToInt16(match.Groups[1].Value));
            }
            ids.Sort();
            log("Retrieved " + ids.Count + " set id");
            return true;
        }

        static void getSets(ref List<string[]> sets, List<short> ids)
        {
            int index = 0;
            while (index < ids.Count)
            {
                var pairs = new List<KeyValuePair<string, string>>();
                pairs.Add(new KeyValuePair<string, string>("expansion_id", ids[index].ToString()));
                string result = "";
                log("Getting set " + ids[index]);
                if (postRequest(pairs, "/jsp/cardlist/expansionHeader", ref result))
                {
                    IHtmlDocument page = new HtmlParser(result).Parse();
                    IElement match = page.DocumentElement.QuerySelector("h3");
                    string[] seperator = new string[] { " - " };
                    string[] split = match.TextContent.Split(seperator, StringSplitOptions.None);
                    sets.Add(new string[] { ids[index].ToString(), split[1], split[0] });
                    index++;
                }
                else
                    if (!prompt("Retry?"))
                        index++;

            }
        }

        static bool requestPage(string uri, ref string result)
        {
            try
            {
                var wclient = new WebClient();
                wclient.Encoding = System.Text.Encoding.UTF8;
                result = new StreamReader(wclient.OpenRead(uri), wclient.Encoding).ReadToEnd();
                return true;
            }
            catch (Exception e)
            {
                log(e.ToString());
            }
            return false;
        }

        static bool postRequest(List<KeyValuePair<string, string>> pairs, string location, ref string result)
        {
            using (var client = new HttpClient())
            {
                client.BaseAddress = new Uri("http://ws-tcg.com");
                var encpair = new FormUrlEncodedContent(pairs);
                try
                {
                    var content = client.PostAsync(location, encpair).Result;
                    result = content.Content.ReadAsStringAsync().Result;
                }
                catch (AggregateException e)
                {
                    log(e.ToString());
                    pause();
                    return false;
                }
            }
            return true;
        }

        static bool prompt(string message)
        {
            log(message + " y/n");
            ConsoleKeyInfo key;
            do
            {
                key = Console.ReadKey();
            } while (key.Key.ToString().ToLower() != "y" && key.Key.ToString().ToLower() != "n");
            Console.WriteLine();

            consolelog.WriteLine(key.Key.ToString());

            if (key.Key.ToString().ToLower() == "y")
                return true;
            else
                return false;
        }

        static void processSets(List<short> setIds, List<string[]> sets, MySqlConnection conn)
        {
            try
            {
                foreach (string[] set in sets)
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
                        default:
                            log("Unknown set identifier: " + set[2] + " please enter value");
                            set[2] = Console.ReadLine().Trim();
                            break;
                    }
                    log("Inserting set " + set[1] + "into database with id " + set[0] + " and type " + set[2]);

                    MySqlCommand cmd = new MySqlCommand();
                    cmd.Connection = conn;

                    cmd.CommandText = "INSERT INTO ws_sets(id,jp_name,type) VALUES(@id,@jp_name,@type)";
                    cmd.Prepare();

                    cmd.Parameters.AddWithValue("@id", set[0]);
                    cmd.Parameters.AddWithValue("@jp_name", set[1]);
                    cmd.Parameters.AddWithValue("@type", set[2]);

                    cmd.ExecuteNonQuery();
                }

            }
            catch (MySqlException e)
            {
                log("Error: " + e.ToString());
            }
        }


        static void Main(string[] args)
        {
            MySqlConnection conn = null;
            consolelog.AutoFlush = true;
            List<short> setIds = new List<short>();
            string cs = @"server=localhost;userid=root;password=;database=ws_db;charset=utf8;";

            try
            {
                conn = new MySqlConnection(cs);
                conn.Open();
            }
            catch (MySqlException e)
            {

                log(e.ToString());
                log("Database connection failed!");
                pause();
                Environment.Exit(1);
            }
            if (getId(ref setIds))
            {
                if (prompt("Get set data?"))
                {
                    List<string[]> sets = new List<string[]>();
                    getSets(ref sets, setIds);
                    processSets(setIds, sets, conn);

                }

                int index = 0;
                while (index < setIds.Count)
                {
                    var pairs = new List<KeyValuePair<string, string>>();
                    pairs.Add(new KeyValuePair<string, string>("expansion_id", setIds[index].ToString()));
                    string result = "";
                    log("Processing set " + setIds[index]);
                    //this is a mess and rather redudant
                    if (postRequest(pairs, "/jsp/cardlist/expansionDetail", ref result))
                    {
                        Serializer serializer = new Serializer();
                        IHtmlDocument page = new HtmlParser(result).Parse();
                        //get the string that has the total number of cards
                        string noCardsText = page.DocumentElement.QuerySelector("p.pageLink").FirstChild.TextContent.Trim();
                        Match nocards = Regex.Match(noCardsText, "\\[(?:\\d)+ - (?:\\d)+ \\/ (\\d+)\\]");
                        //calculate the number of pages
                        int nopages = (int)Math.Ceiling((Convert.ToDouble(nocards.Groups[1].Value) / 10));
                        log("Set " + setIds[index] + " has " + nopages + " pages");
                        //get all cards on this page
                        IEnumerable<IElement> matches = page.DocumentElement.QuerySelectorAll("tr td:first-child");
                        //store all the ids in a list
                        List<string> cardSetIds = new List<string>();
                        foreach (IElement m in matches)
                            if (!String.IsNullOrWhiteSpace(m.TextContent))
                                cardSetIds.Add(m.TextContent.Trim());

                        for (int i = 2; i <= nopages; i++)
                        {
                            pairs = new List<KeyValuePair<string, string>>();
                            pairs.Add(new KeyValuePair<string, string>("expansion_id", setIds[index].ToString()));
                            pairs.Add(new KeyValuePair<string, string>("page", i.ToString()));
                            if (postRequest(pairs, "/jsp/cardlist/expansionDetail", ref result))
                            {
                                page = new HtmlParser(result).Parse();
                                matches = page.DocumentElement.QuerySelectorAll("tr td:first-child");
                                foreach (IElement m in matches)
                                    if (!String.IsNullOrWhiteSpace(m.TextContent))
                                        cardSetIds.Add(m.TextContent.Trim());
                            }
                            else
                                if (prompt("Retry?"))
                                    i--;
                        }
                        foreach (string id in cardSetIds)
                        {
                            requestPage("http://ws-tcg.com/cardlist/?cardno=" + id, ref result);
                            page = new HtmlParser(result).Parse();
                            string image = page.DocumentElement.QuerySelector(".status td img").Attributes.First(attr => attr.Name == "src").Value;
                            matches = page.DocumentElement.QuerySelectorAll(".status td");
                            MatchCollection rmatches;
                            List<IElement> tableNodes = matches.ToList<IElement>();
                            Dictionary<string, string> card = new Dictionary<string, string>();
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
                                default:
                                    log("Unknown card type: " + tableNodes[6].TextContent.Trim() + " please enter a value");
                                    card.Add("type", Console.ReadLine().Trim());
                                    break;
                            }
                            card.Add("color", Regex.Match(tableNodes[7].InnerHtml.Trim(), "src=['\"](?:.+\\/)+(\\w)").Groups[1].Value.ToUpper());
                            card.Add("level", Regex.IsMatch(tableNodes[8].TextContent.Trim(), "[-－]") ? null : tableNodes[8].TextContent.Trim());
                            card.Add("cost", Regex.IsMatch(tableNodes[9].TextContent.Trim(), "[-－]") ? null : tableNodes[9].TextContent.Trim());
                            card.Add("power", Regex.IsMatch(tableNodes[10].TextContent.Trim(), "[-－]") ? null : tableNodes[10].TextContent.Trim());
                            card.Add("soul", Regex.Matches(tableNodes[11].InnerHtml.Trim(), "src=['\"](?:.+?\\/)+?(\\w+)?\\.").Count.ToString());
                            ArrayList triggers = new ArrayList();
                            rmatches = Regex.Matches(tableNodes[12].InnerHtml, "src=['\"](?:.+?\\/)+?(\\w+)?\\.");
                            foreach (Match m in rmatches)
                                triggers.Add(m.Groups[1].Value);
                            card.Add("triggers", triggers.Count == 0 ? null : serializer.Serialize(triggers));
                            ArrayList traits = new ArrayList();
                            if (tableNodes[13].TextContent.Trim().Contains('・'))
                            {
                                string[] s = tableNodes[13].TextContent.Split('・');
                                foreach (string item in s)
                                {
                                    if (!Regex.IsMatch(item.Trim(), "[-－]"))
                                        traits.Add(item.Trim());
                                }
                            }
                            card.Add("traits", traits.Count == 0 ? null : serializer.Serialize(traits));

                            if (!Regex.IsMatch(tableNodes[14].TextContent.Trim(), "(?:（バニラ）|[-－])"))
                                card.Add("text", Regex.Replace(tableNodes[14].InnerHtml.Trim(), "<br\\s*(?:\\/)*>$", "", RegexOptions.None));
                            else
                                card.Add("text", null);

                            if (!Regex.IsMatch(tableNodes[14].TextContent.Trim(), "[-－]"))
                                card.Add("flavor", Regex.Replace(tableNodes[15].InnerHtml.Trim(), "<br\\s*(?:\\/)*>$", "", RegexOptions.None));
                            else
                                card.Add("flavor", null);

                            log("Adding " + id + " to the database.");
                            MySqlCommand cmd = new MySqlCommand();
                            cmd.Connection = conn;

                            cmd.CommandText = "INSERT INTO " +
                                "ws_cards(cardno,name,kana,rarity,expansion,side,color,level,cost,power,soul,triggers,traits,text,flavor) " +
                                "VALUES(@cardno,@name,@kana,@rarity,@expansion,@side,@color,@level,@cost,@power,@soul,@triggers,@traits,@text,@flavor)";
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
                        index++;
                    }
                    else
                        if (!prompt("Retry?"))
                            index++;

                }
            }
            //close mysql connection
            if (conn != null)
            {
                conn.Close();
            }
            pause();
        }
    }
}
