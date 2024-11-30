﻿// <auto-generated />
using System;
using KeyPulse.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace KeyPulse.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20241130091627_ReplaceConnectionsWithDeviceEvents")]
    partial class ReplaceConnectionsWithDeviceEvents
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "8.0.10")
                .HasAnnotation("Proxies:ChangeTracking", false)
                .HasAnnotation("Proxies:CheckEquality", false)
                .HasAnnotation("Proxies:LazyLoading", true);

            modelBuilder.Entity("KeyPulse.Models.DeviceEvent", b =>
                {
                    b.Property<int>("DeviceEventId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("DeviceId")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("EventType")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("Timestamp")
                        .HasColumnType("TEXT");

                    b.HasKey("DeviceEventId");

                    b.HasIndex("Timestamp")
                        .HasDatabaseName("Idx_DeviceEvents_Timestamp");

                    b.HasIndex("DeviceId", "Timestamp")
                        .HasDatabaseName("Idx_DeviceEvents_DeviceIdTimestamp");

                    b.HasIndex("DeviceId", "Timestamp", "EventType")
                        .IsUnique()
                        .HasDatabaseName("Idx_DeviceEvents_Unique");

                    b.ToTable("DeviceEvents", (string)null);
                });

            modelBuilder.Entity("KeyPulse.Models.DeviceInfo", b =>
                {
                    b.Property<string>("DeviceId")
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceName")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("DeviceType")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("IsActive")
                        .HasColumnType("INTEGER");

                    b.Property<string>("PID")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("VID")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.HasKey("DeviceId");

                    b.HasIndex("DeviceId")
                        .HasDatabaseName("Idx_Devices_DeviceId");

                    b.ToTable("Devices", (string)null);
                });

            modelBuilder.Entity("KeyPulse.Models.DeviceEvent", b =>
                {
                    b.HasOne("KeyPulse.Models.DeviceInfo", "Device")
                        .WithMany("DeviceEventList")
                        .HasForeignKey("DeviceId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Device");
                });

            modelBuilder.Entity("KeyPulse.Models.DeviceInfo", b =>
                {
                    b.Navigation("DeviceEventList");
                });
#pragma warning restore 612, 618
        }
    }
}
