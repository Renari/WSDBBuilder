using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MySql.Data.MySqlClient;

namespace WSDBBuilder
{
    class Program
    {
        static StreamWriter consolelog = new StreamWriter("output.log");
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
            MatchCollection matches = Regex.Matches(result, "showExpansionDetail\\('(\\d(?:(?:\\d)?)+)'");
            foreach (Match match in matches)
            {
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
                    Match match = Regex.Match(result, "<h3.+?>(.+?)<\\/h3>");
                    string[] seperator = new string[] { " - " };
                    string[] split = match.Groups[1].Value.Split(seperator, StringSplitOptions.None);
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
                result = new StreamReader(wclient.OpenRead(uri), wclient.Encoding).ReadToEnd();
                return true;
            }
            catch(Exception e)
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
                                if (db == true)
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
                        Match nocards = Regex.Match(result, "\\[\\d(?:(?:\\d)?)+ - \\d(?:(?:\\d)?)+ \\/ (\\d(?:(?:\\d)?)+)\\]");
                        int nopages = (int)Math.Ceiling((Convert.ToDouble(nocards.Groups[1].Value) / 10));
                        log("Set " + setIds[index] + " has " + nopages + " pages");
                        List<string> cardSetIds = new List<string>();
                        MatchCollection matches = Regex.Matches(result, "<tr>(?:\\r)?\\n(?:(?: )+)?<td>(.+?)<\\/td>");
                        foreach (Match m in matches)
                            cardSetIds.Add(m.Groups[1].Value);

                        for (int i = 2; i <= nopages; i++)
                        {
                            pairs = new List<KeyValuePair<string, string>>();
                            pairs.Add(new KeyValuePair<string, string>("expansion_id", setIds[index].ToString()));
                            pairs.Add(new KeyValuePair<string, string>("page", i.ToString()));
                            if (postRequest(pairs, "/jsp/cardlist/expansionDetail", ref result))
                            {
                                matches = Regex.Matches(result, "<tr>(?:\\r)?\\n(?:(?: )+)?<td>(.+?)<\\/td>");
                                foreach (Match m in matches)
                                    cardSetIds.Add(m.Groups[1].Value);
                            }
                            else
                                if (prompt("Retry?"))
                                    i--;
                        }
                        foreach (string id in cardSetIds)
                        {
                            requestPage("http://ws-tcg.com/cardlist/?cardno=" + id, ref result);

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
