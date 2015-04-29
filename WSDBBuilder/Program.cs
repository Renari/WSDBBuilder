using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Fizzler.Systems.HtmlAgilityPack;
using HtmlAgilityPack;
using MySql.Data.MySqlClient;

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
            HtmlDocument page = new HtmlDocument();
            page.LoadHtml2(result);
            IEnumerable<HtmlNode> matches = page.DocumentNode.QuerySelectorAll("a[onclick^=showExpansionDetail]");
            foreach (HtmlNode node in matches)
            {
                HtmlAttribute att = node.Attributes["onclick"];
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
                    HtmlDocument page = new HtmlDocument();
                    page.LoadHtml2(result);
                    HtmlNode match = page.DocumentNode.QuerySelector("h3");
                    string[] seperator = new string[] { " - " };
                    string[] split = match.InnerText.Split(seperator, StringSplitOptions.None);
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


        static void Main(string[] args)
        {
            MySqlConnection conn = null;
            consolelog.AutoFlush = true;
            List<short> setIds = new List<short>();
            List<string[]> sets = new List<string[]>();
            string cs = @"server=localhost;userid=root;password=;database=ws_db;charset=utf8;";
            bool db = false;

            try
            {
                conn = new MySqlConnection(cs);
                conn.Open();
                db = true;
            }
            catch (MySqlException e)
            {

                log(e.ToString());
                if (!prompt("Unable to connect to the database, continue?"))
                    Environment.Exit(0);
            }
            if (getId(ref setIds))
            {
                bool processSets = prompt("Get set data?");
                if (processSets)
                {
                    getSets(ref sets, setIds);
                    try
                    {
                        if (db)
                        {
                            foreach (string[] set in sets)
                            {
                                switch (set[2])
                                {
                                    case "ブースターパック":
                                        set[2] = "bp";
                                        break;
                                    case "エクストラパック":
                                        set[2] = "ep";
                                        break;
                                    case "トライアルデッキ":
                                        set[2] = "td";
                                        break;
                                    case "PRカード":
                                        set[2] = "pr";
                                        break;
                                    case "エクストラパック／エクストラブースター/他":
                                        set[2] = "ot";
                                        break;
                                    default:
                                        log("Unknown set identifier: " + set[2] + " please enter value");
                                        set[2] = Console.ReadLine();
                                        break;
                                }
                                if (db)
                                {
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
                        }

                    }
                    catch (MySqlException e)
                    {
                        log("Error: " + e.ToString());
                    }
                }

                int index = 0;
                while (index < 1)//ids.Count)
                {
                    var pairs = new List<KeyValuePair<string, string>>();
                    pairs.Add(new KeyValuePair<string, string>("expansion_id", setIds[index].ToString()));
                    string result = "";
                    log("Processing set " + setIds[index]);
                    //this is a mess and rather redudant
                    if (postRequest(pairs, "/jsp/cardlist/expansionDetail", ref result))
                    {

                        HtmlDocument page = new HtmlDocument();
                        page.LoadHtml2(result);
                        //get the string that has the total number of cards
                        string noCardsText = page.DocumentNode.QuerySelector("p.pageLink").InnerText.Split(new string[] {Environment.NewLine}, StringSplitOptions.None)[1];
                        Match nocards = Regex.Match(noCardsText, "\\[(?:\\d)+ - (?:\\d)+ \\/ (\\d+)\\]");
                        //calculate the number of pages
                        int nopages = (int)Math.Ceiling((Convert.ToDouble(nocards.Groups[1].Value) / 10));
                        log("Set " + setIds[index] + " has " + nopages + " pages");
                        //get all cards on this page
                        IEnumerable<HtmlNode> matches = page.DocumentNode.QuerySelectorAll("tr td:first-child");
                        //store all the ids in a list
                        List<string> cardSetIds = new List<string>();
                        foreach (HtmlNode m in matches)
                            cardSetIds.Add(m.InnerText);

                        for (int i = 2; i <= nopages; i++)
                        {
                            pairs = new List<KeyValuePair<string, string>>();
                            pairs.Add(new KeyValuePair<string, string>("expansion_id", setIds[index].ToString()));
                            pairs.Add(new KeyValuePair<string, string>("page", i.ToString()));
                            if (postRequest(pairs, "/jsp/cardlist/expansionDetail", ref result))
                            {
                                page.LoadHtml2(result);
                                matches = page.DocumentNode.QuerySelectorAll("tr td:first-child");
                                foreach (HtmlNode m in matches)
                                    cardSetIds.Add(m.InnerText);
                            }
                            else
                                if (prompt("Retry?"))
                                    i--;
                        }
                        /*foreach (string id in cardSetIds)
                        {
                            try
                            {
                                requestPage("http://ws-tcg.com/cardlist/?cardno=" + "FS/S34-032", ref result);
                                //Match match = Regex.Match(result, "<th(?:.+?)?>カード名<\\/th>(?:\\r)?\\n(?:(?: )+)?<td(?:.+?)?>(?:\\r)?\\n(.+?)<br(?: )?(?:\\/)?>(?:\\r)?\\n<(?:.+?)>(.+?)<\\/(?:.+)>");
                                Match match = Regex.Match(result, "<table class=\"status\">.*?<img src=\"(?<image>[^\"']*)\"(?:[^>]+>){8}(?:\r)?\n(?<name>[^<]+)(?:[^>]+>){2}(?<kana>[^<]+)(?:[^>]+>){7}(?<number>[^<]+)(?:[^>]+>){2}(?:[^<]+)(?:[^>]+>){2}(?<rarity>[^<]+)(?:[^>]+>){6}(?<expansion>[^<]+)(?:[^>]+>){4}[^<]+<img src=[\"'][^'\"]+(?<side\\w)\\.gif(?:[^>]+>){7}(?<type>[^<]+)(?:[^>]+>){4}<img src=[\"'][^'\"]+\\/(?<color>\\w)(?:[^>]+>){7}(?<level>[^<]+)(?:[^>]+>){4}(?<cost>[^<]+)(?:[^>]+>){6}(?<power>[^<]+)(?:[^>]+>){4}(?<soul>.+?)(?:<\\/td>)(?:[^>]+>){5}(?<trigger>.+?)(?:<\\/td>)(?:[^>]+>){3}(?:\r)?(?:\n)(?<traits>[^>]+)(?:\r)?(?:\n)<(?:[^>]+>){6}(?<text>.+?)<\\/td>(?:[^>]+>){5}(?<flavor>.+?)<\\/td>", RegexOptions.Singleline);
                                foreach (string item in match.Groups)
                                {
                                    log(item);
                                }
                                //match = Regex.Match(result, )
                            }
                            catch (ArgumentException e)
                            {
                                log(e.ToString());
                            }
                            break;
                        }*/
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
