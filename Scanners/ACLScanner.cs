﻿using PingCastle.ADWS;
using PingCastle.Graph.Database;
using PingCastle.Graph.Export;
using PingCastle.Graph.Reporting;
using PingCastle.misc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.DirectoryServices;
using System.IO;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace PingCastle.Scanners
{
    public class ACLScanner : IScanner
    {
        public string Name { get { return "aclcheck"; } }
        public string Description { get { return "Check authorization related to users or groups. Default to everyone, authenticated users and domain users"; } }

        public string Server { get; private set; }
        public int Port { get; private set; }
        public NetworkCredential Credential { get; private set; }

        List<KeyValuePair<SecurityIdentifier, string>> UsersToMatch;
        public static List<string> UserList = new List<string>();

        public void Initialize(string server, int port, NetworkCredential credential)
        {
            Server = server;
            Port = port;
            Credential = credential;
        }

        Guid LAPSSchemaId = Guid.Empty;

        public void Export(string filename)
        {
            DisplayAdvancement("Starting");

            using (ADWebService adws = new ADWebService(Server, Port, Credential))
            {
                string[] propertiesLaps = new string[] { "schemaIDGUID" };
                // note: the LDAP request does not contain ms-MCS-AdmPwd because in the old time, MS consultant was installing customized version of the attriute, * being replaced by the company name
                // check the oid instead ? (which was the same even if the attribute name was not)
                adws.Enumerate(adws.DomainInfo.SchemaNamingContext, "(name=ms-*-AdmPwd)", propertiesLaps, (ADItem aditem) =>
                {
                    LAPSSchemaId = aditem.SchemaIDGUID;
                }, "OneLevel");

                using (StreamWriter sw = File.CreateText(filename))
                {
                    sw.WriteLine("DistinguishedName\tIdentity\tAccessRule");
                    var domainInfo = adws.DomainInfo;
                    EnrichDomainInfo(adws, domainInfo);
                    BuildUserList(adws, domainInfo);

                    WorkOnReturnedObjectByADWS callback = ((ADItem x)
                    =>
                    {
                        if (x.NTSecurityDescriptor != null)
                        {
                            var Owner = x.NTSecurityDescriptor.GetOwner(typeof(SecurityIdentifier));
                            var match = MatchesUsersToCheck(Owner);
                            if (match != null)
                            {
                                sw.WriteLine(x.DistinguishedName + "\t" + match.Value.Value + "\tOwner");
                            }
                            foreach (ActiveDirectoryAccessRule accessrule in x.NTSecurityDescriptor.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                            {
                                // ignore audit / denied ace
                                if (accessrule.AccessControlType != AccessControlType.Allow)
                                    continue;
                                match = MatchesUsersToCheck(accessrule.IdentityReference);
                                if (!match.HasValue)
                                    continue;
                                if (MatchesBadACL(accessrule))
                                {
                                    sw.WriteLine(x.DistinguishedName + "\t" + match.Value.Value + "\t" + accessrule.ActiveDirectoryRights.ToString());
                                }


                            }
                        }
                    }
                    );
                    DisplayAdvancement("Analyzing AD Objects");
                    adws.Enumerate(domainInfo.DefaultNamingContext, "(objectClass=*)", new string[] { "distinguishedName", "nTSecurityDescriptor" }, callback);
                    DisplayAdvancement("Analyzing files");
                    CheckFilePermission(domainInfo, sw, adws);
                    DisplayAdvancement("Done");
                }
            }
        }

        private void EnrichDomainInfo(ADWebService adws, ADDomainInfo domainInfo)
        {
            // adding the domain sid
            string[] properties = new string[] {"objectSid",
            };
            WorkOnReturnedObjectByADWS callback =
                (ADItem aditem) =>
                {
                    domainInfo.DomainSid = aditem.ObjectSid;
                };

            adws.Enumerate(domainInfo.DefaultNamingContext,
                                            "(&(objectClass=domain)(distinguishedName=" + domainInfo.DefaultNamingContext + "))",
                                            properties, callback, "Base");
        }

        private void BuildUserList(ADWebService adws, ADDomainInfo domainInfo)
        {
            UsersToMatch = new List<KeyValuePair<SecurityIdentifier, string>>();
            if (UserList.Count == 0)
            {
                UsersToMatch.Add(new KeyValuePair<SecurityIdentifier, string>(new SecurityIdentifier("S-1-1-0"), GraphObjectReference.Everyone));
                UsersToMatch.Add(new KeyValuePair<SecurityIdentifier, string>(new SecurityIdentifier("S-1-5-7"), GraphObjectReference.Anonymous));
                UsersToMatch.Add(new KeyValuePair<SecurityIdentifier, string>(new SecurityIdentifier("S-1-5-11"), GraphObjectReference.AuthenticatedUsers));
                UsersToMatch.Add(new KeyValuePair<SecurityIdentifier, string>(new SecurityIdentifier("S-1-5-32-545"), GraphObjectReference.Users));
                UsersToMatch.Add(new KeyValuePair<SecurityIdentifier, string>(new SecurityIdentifier(domainInfo.DomainSid.Value + "-513"), GraphObjectReference.DomainUsers));
                UsersToMatch.Add(new KeyValuePair<SecurityIdentifier, string>(new SecurityIdentifier(domainInfo.DomainSid.Value + "-515"), GraphObjectReference.DomainComputers));
                return;
            }
            foreach (var user in UserList)
            {
                var aditem = Search(adws, domainInfo, user);
                if (aditem == null)
                {
                    DisplayAdvancement(user + " was not found");
                    continue;
                }
                if (aditem.ObjectSid == null)
                {
                    DisplayAdvancement(user + " has been found but it is not an object with a SID and thus cannot be searched for");
                    continue;
                }
                UsersToMatch.Add(new KeyValuePair<SecurityIdentifier, string>(aditem.ObjectSid, user));
            }
            if (UsersToMatch.Count == 0)
            {
                throw new PingCastleException("The scanner has not ACL to search for");
            }
        }

        public Program.DisplayState QueryForAdditionalParameterInInteractiveMode()
        {
            string input = null;
            UserList.Clear();
            do
            {
                ConsoleMenu.Title = "Enter users or groups to check";
                ConsoleMenu.Information = @"This scanner enumerate all objects' where a user or a group have write access.
You can enter many users or groups. Enter them one by one and complete with an empty line. SAMAccountName or SID are accepted.
Or just press enter to use the default (Everyone, Anonymous, Builtin\\Users, Authenticated Users and Domain Users groups).";
                input = ConsoleMenu.AskForString();
                if (!String.IsNullOrEmpty(input))
                {
                    UserList.Add(input);
                }
                else
                {
                    break;
                }
            } while (true);
            return Program.DisplayState.AskForServer;
        }

        private KeyValuePair<SecurityIdentifier, string>? MatchesUsersToCheck(IdentityReference Owner)
        {
            SecurityIdentifier sid = (SecurityIdentifier)Owner;
            foreach (var user in UsersToMatch)
            {
                if (sid == user.Key)
                    return user;
            }
            return null;
        }

        private ADItem Search(ADWebService adws, ADDomainInfo domainInfo, string userName)
        {
            ADItem output = null;
            string[] properties = new string[] {
                        "distinguishedName",
                        "displayName",
                        "name",
                        "objectSid",
            };
            WorkOnReturnedObjectByADWS callback =
                    (ADItem aditem) =>
                    {
                        output = aditem;
                    };

            if (userName.StartsWith("S-1-5"))
            {
                adws.Enumerate(domainInfo.DefaultNamingContext,
                                                "(objectSid=" + ADConnection.EncodeSidToString(userName) + ")",
                                                properties, callback);
            }

            adws.Enumerate(domainInfo.DefaultNamingContext,
                                            "(sAMAccountName=" + ADConnection.EscapeLDAP(userName) + ")",
                                            properties, callback);
            if (output != null)
                return output;
            adws.Enumerate(domainInfo.DefaultNamingContext,
                                            "(cn=" + ADConnection.EscapeLDAP(userName) + ")",
                                            properties, callback);
            if (output != null)
                return output;
            adws.Enumerate(domainInfo.DefaultNamingContext,
                                            "(displayName=" + ADConnection.EscapeLDAP(userName) + ")",
                                            properties, callback);
            if (output != null)
                return output;
            return output;
        }

        bool MatchesBadACL(ActiveDirectoryAccessRule accessrule)
        {
            // ADS_RIGHT_GENERIC_ALL
            if ((accessrule.ActiveDirectoryRights & ActiveDirectoryRights.GenericAll) == ActiveDirectoryRights.GenericAll)
            {
                return true;
            }
            // ADS_RIGHT_GENERIC_WRITE
            if ((accessrule.ActiveDirectoryRights & ActiveDirectoryRights.GenericWrite) == ActiveDirectoryRights.GenericWrite)
            {
                return true;
            }
            // ADS_RIGHT_WRITE_DAC
            if ((accessrule.ActiveDirectoryRights & ActiveDirectoryRights.WriteDacl) == ActiveDirectoryRights.WriteDacl)
            {
                return true;
            }
            // ADS_RIGHT_WRITE_OWNER
            if ((accessrule.ActiveDirectoryRights & ActiveDirectoryRights.WriteOwner) == ActiveDirectoryRights.WriteOwner)
            {
                return true;
            }
            if (accessrule.ObjectFlags == ObjectAceFlags.None)
            {
                // ADS_RIGHT_DS_CONTROL_ACCESS
                if ((accessrule.ActiveDirectoryRights & ActiveDirectoryRights.ExtendedRight) == ActiveDirectoryRights.ExtendedRight)
                {
                    return true;
                }
                // ADS_RIGHT_DS_SELF
                if ((accessrule.ActiveDirectoryRights & ActiveDirectoryRights.Self) == ActiveDirectoryRights.Self)
                {
                    return true;
                }
                // ADS_RIGHT_DS_WRITE_PROP
                if ((accessrule.ActiveDirectoryRights & ActiveDirectoryRights.WriteProperty) == ActiveDirectoryRights.WriteProperty)
                {
                    return true;
                }
            }
            else if ((accessrule.ObjectFlags & ObjectAceFlags.ObjectAceTypePresent) == ObjectAceFlags.ObjectAceTypePresent)
            {
                // ADS_RIGHT_DS_CONTROL_ACCESS
                if ((accessrule.ActiveDirectoryRights & ActiveDirectoryRights.ExtendedRight) == ActiveDirectoryRights.ExtendedRight)
                {
                    foreach (KeyValuePair<Guid, RelationType> extendedright in RelationFactory.GuidsControlExtendedRights)
                    {
                        if (extendedright.Key == accessrule.ObjectType)
                        {
                            return true;
                        }
                    }
                }
                // ADS_RIGHT_DS_SELF
                if ((accessrule.ActiveDirectoryRights & ActiveDirectoryRights.Self) == ActiveDirectoryRights.Self)
                {
                    foreach (KeyValuePair<Guid, RelationType> validatewrite in RelationFactory.GuidsControlValidatedWrites)
                    {
                        if (validatewrite.Key == accessrule.ObjectType)
                        {
                            return true;
                        }
                    }
                }
                // ADS_RIGHT_DS_WRITE_PROP
                if ((accessrule.ActiveDirectoryRights & ActiveDirectoryRights.WriteProperty) == ActiveDirectoryRights.WriteProperty)
                {
                    foreach (KeyValuePair<Guid, RelationType> controlproperty in RelationFactory.GuidsControlProperties)
                    {
                        if (controlproperty.Key == accessrule.ObjectType)
                        {
                            return true;
                        }
                    }
                    foreach (KeyValuePair<Guid, RelationType> controlpropertyset in RelationFactory.GuidsControlPropertiesSets)
                    {
                        if (controlpropertyset.Key == accessrule.ObjectType)
                        {
                            return true;
                        }
                    }
                }
                // ADS_RIGHT_DS_READ_PROP
                if ((accessrule.ActiveDirectoryRights & ActiveDirectoryRights.ReadProperty) == ActiveDirectoryRights.ReadProperty)
                {
                    if (LAPSSchemaId == accessrule.ObjectType)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        void CheckFilePermission(ADDomainInfo domainInfo, StreamWriter sw, ADWebService adws)
        {
            var pathToCheck = new List<string>();
            foreach (var script in Directory.GetDirectories(@"\\" + domainInfo.DnsHostName + @"\SYSVOL\" + domainInfo.DomainName + @"\scripts", "*", SearchOption.TopDirectoryOnly))
            {
                pathToCheck.Add(script);
            }
            foreach (var gpo in Directory.GetDirectories(@"\\" + domainInfo.DnsHostName + @"\SYSVOL\" + domainInfo.DomainName + @"\policies", "*", SearchOption.TopDirectoryOnly))
            {
                pathToCheck.Add(gpo);

            }

            BlockingQueue<string> queue = new BlockingQueue<string>(200);
            int numberOfThread = 20;
            Thread[] threads = new Thread[numberOfThread];
            try
            {

                ThreadStart threadFunction = () =>
                {
                    adws.ThreadInitialization();
                    for (; ; )
                    {
                        string path = null;
                        if (!queue.Dequeue(out path)) break;
                        try
                        {
                            CheckFilePermissionWithPath(domainInfo, sw, path);
                        }
                        catch (Exception ex)
                        {
                            DisplayAdvancement("Error while working with " + path + " (" + ex.Message + ")");
                        }
                    }
                };

                // Consumers
                for (int i = 0; i < numberOfThread; i++)
                {
                    threads[i] = new Thread(threadFunction);
                    threads[i].Start();
                }

                foreach (string path in pathToCheck)
                {
                    queue.Enqueue(path);
                }
                queue.Quit();
                Trace.WriteLine("examining file completed. Waiting for worker thread to complete");
                for (int i = 0; i < numberOfThread; i++)
                {
                    threads[i].Join();
                }
                Trace.WriteLine("Done insert file");

            }
            finally
            {
                queue.Quit();
                for (int i = 0; i < numberOfThread; i++)
                {
                    if (threads[i] != null)
                        if (threads[i].ThreadState == System.Threading.ThreadState.Running)
                            threads[i].Abort();
                }
            }
        }

        void CheckFilePermissionWithPath(ADDomainInfo domainInfo, StreamWriter sw, string path)
        {
            if (!Directory.Exists(path))
                return;
            var dirs = new List<string>(Directory.GetDirectories(path, "*", SearchOption.AllDirectories));
            dirs.Insert(0, path);
            foreach (var dirname in dirs)
            {
                try
                {
                    AnalyzeAccessControl(sw, Directory.GetAccessControl(dirname), dirname, (path == dirname));
                }
                catch (Exception)
                {
                }
            }
            foreach (var filename in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories))
            {
                try
                {
                    AnalyzeAccessControl(sw, File.GetAccessControl(filename), filename, false);
                }
                catch (Exception)
                {
                }
            }
        }

        void AnalyzeAccessControl(StreamWriter sw, FileSystemSecurity security, string name, bool includeInherited)
        {
            var Owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier));
            var matchOwner = MatchesUsersToCheck(Owner);
            if (matchOwner.HasValue)
                sw.WriteLine(name + "\t" + matchOwner.Value.Value + "\tOwner");
            var accessRules = security.GetAccessRules(true, includeInherited, typeof(SecurityIdentifier));
            if (accessRules == null)
                return;

            foreach (FileSystemAccessRule accessrule in accessRules)
            {
                if (accessrule.AccessControlType == AccessControlType.Deny)
                    continue;
                if ((FileSystemRights.Write & accessrule.FileSystemRights) != FileSystemRights.Write)
                    continue;

                var match = MatchesUsersToCheck(accessrule.IdentityReference);
                if (!match.HasValue)
                    continue;
                sw.WriteLine(name + "\t" + match.Value.Value + "\t" + accessrule.FileSystemRights.ToString());
            }
        }

        private static void DisplayAdvancement(string data)
        {
            string value = "[" + DateTime.Now.ToLongTimeString() + "] " + data;
            Console.WriteLine(value);
            Trace.WriteLine(value);
        }
    }
}
